using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Ven4Tools.Models;

namespace Ven4Tools.Services
{
    public class AvailabilityChecker : IDisposable
    {
        // Один общий HttpClient на приложение: пересоздание на каждый инстанс
        // приводит к socket exhaustion (рекомендация MS).
        private static readonly HttpClient SharedHttpClient = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestHeaders = { { "User-Agent", "Ven4Tools" } }
        };

        private readonly HttpClient _httpClient;
        private readonly ConcurrentDictionary<string, CachedAvailability> cache = new();
        private readonly TimeSpan cacheDuration = TimeSpan.FromMinutes(5);

        private const long DefaultUnknownSizeMB = InstallSizeDefaults.UnknownSizeMB;
        // Таймаут хранится отдельно и применяется per-request через CancellationTokenSource:
        // менять HttpClient.Timeout после первого запроса нельзя (InvalidOperationException)
        private volatile int _timeoutSeconds;

        // Разбор строки размера из вывода winget show. Паттерн статический, а метод
        // вызывается для каждой строки вывода по каждому проверяемому приложению
        // (сотни приложений при проверке доступности), поэтому компилируем один раз,
        // а не пересобираем Regex на каждый вызов.
        private static readonly System.Text.RegularExpressions.Regex _wingetSizeRegex =
            new(@"(\d+[,.]?\d*)\s*(MB|KB|GB)",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        public AvailabilityChecker() : this(SharedHttpClient)
        {
        }

        // internal — сюда заходят тесты классификации GetUrlInfo с подменным
        // HttpMessageHandler, минуя реальную сеть и не трогая общий статический
        // клиент (см. комментарий выше про socket exhaustion).
        internal AvailabilityChecker(HttpClient httpClient)
        {
            _httpClient = httpClient;
            _timeoutSeconds = Math.Max(5, AppSettings.CheckTimeout);
        }

        public void UpdateTimeout(int seconds)
        {
            _timeoutSeconds = Math.Max(5, seconds);
        }

        private class CachedAvailability
        {
            public AvailabilityStatus Status { get; set; }
            public long SizeMB { get; set; }
            public DateTime Timestamp { get; set; }
        }

        public enum AvailabilityStatus
        {
            Unknown,
            Available,
            Unavailable,
            RegionBlocked
        }

        public class AppAvailabilityResult
        {
            public AvailabilityStatus Status { get; set; }
            public long SizeMB { get; set; }
            public string? Source { get; set; }
        }

        public async Task<(AvailabilityStatus Status, long SizeMB)> CheckAppAvailabilityWithSize(AppInfo app)
        {
            // Параноидальный режим: проверка доступности — это ни загрузка каталога,
            // ни сама установка, поэтому сетевые запросы к сторонним хостам (HEAD/GET)
            // и внешний winget-source здесь запрещены. Возвращаем нейтральный статус
            // «неизвестно»: индикатор в каталоге станет серым, но чекбокс останется
            // активным — установка (одно из двух разрешённых исключений) не блокируется.
            if (ProfileService.Current.ParanoidMode)
                return (AvailabilityStatus.Unknown, 0);

            // Офлайн: сеть не трогаем, отвечаем только по локальному кэшу
            if (OfflineService.IsOffline)
            {
                if (OfflineService.HasCachedInstaller(app.Id))
                    return (AvailabilityStatus.Available, OfflineService.GetCachedInstallerSizeMB(app.Id));
                return (AvailabilityStatus.Unknown, 0);
            }

            string wingetId = !string.IsNullOrEmpty(app.AlternativeId)
                ? app.AlternativeId
                : app.Id ?? string.Empty;

            // Ключ кэша — итоговый идентификатор, чтобы смена AlternativeId
            // корректно обесценивала прежнюю запись
            string cacheKey = wingetId;

            if (cache.TryGetValue(cacheKey, out var cached) && DateTime.Now - cached.Timestamp < cacheDuration)
                return (cached.Status, cached.SizeMB);

            (AvailabilityStatus Status, long SizeMB) result = (AvailabilityStatus.Unavailable, 0);

            if (!string.IsNullOrEmpty(wingetId) && !wingetId.StartsWith("User."))
                result = await GetWingetPackageInfo(wingetId);

            if (result.Status != AvailabilityStatus.Available && app.InstallerUrls != null && app.InstallerUrls.Count > 0)
            {
                bool hasRegionNote = !string.IsNullOrWhiteSpace(app.RegionNote);
                foreach (var url in app.InstallerUrls)
                {
                    var urlResult = await GetUrlInfo(url, hasRegionNote);
                    if (urlResult.Status == AvailabilityStatus.Available)
                    {
                        result = urlResult;
                        break;
                    }

                    // RegionBlocked — тоже настоящий замер (реальный 451/403+сноска с
                    // машины пользователя), а не выдумка каталога, поэтому переживает
                    // переход к следующей ссылке. Обычный Unavailable по-прежнему
                    // отбрасывается — так же вело себя это место до региональной проверки.
                    if (urlResult.Status == AvailabilityStatus.RegionBlocked)
                        result = urlResult;
                }
            }

            // Choco — последний в цепочке, а не первый: winget и прямая ссылка
            // не требуют стороннего пакетного менеджера, choco в некоторых случаях
            // требует его установки первым (см. InstallFromChocoAsync). Без этой
            // проверки приложения без winget/URL (только chocoId) навсегда
            // помечались бы недоступными, хотя реально ставятся через choco.
            if (result.Status != AvailabilityStatus.Available && !string.IsNullOrWhiteSpace(app.ChocoId))
            {
                var chocoResult = await GetChocoPackageInfo(app.ChocoId);
                // RegionBlocked, подтверждённый прямой ссылкой, не должен молча
                // стереться неудачным результатом Chocolatey — только настоящий
                // Available перекрывает уже установленный геоблок.
                if (chocoResult.Status == AvailabilityStatus.Available || result.Status != AvailabilityStatus.RegionBlocked)
                    result = chocoResult;
            }

            CacheResult(cacheKey, new AppAvailabilityResult { Status = result.Status, SizeMB = result.SizeMB });
            return result;
        }

        private void CacheResult(string appId, AppAvailabilityResult result)
        {
            var entry = new CachedAvailability
            {
                Status = result.Status,
                SizeMB = result.SizeMB,
                Timestamp = DateTime.Now
            };
            cache.AddOrUpdate(appId, entry, (_, __) => entry);
        }

        private async Task<(AvailabilityStatus Status, long SizeMB)> GetWingetPackageInfo(string appId)
        {
            try
            {
                if (!CommandLineGuard.ValidateId(appId))
                    return (AvailabilityStatus.Unavailable, 0);

                var (exitCode, output) = await WingetRunner.RunAsync(
                    WingetArgs.Query("show", "--id", appId, "--exact", "--source", "winget"));

                bool success = exitCode == 0 &&
                               (output.Contains("Version", StringComparison.OrdinalIgnoreCase) ||
                                output.Contains("Found", StringComparison.OrdinalIgnoreCase) ||
                                output.Contains("Версия", StringComparison.OrdinalIgnoreCase) ||
                                output.Contains("Найдено", StringComparison.OrdinalIgnoreCase));

                if (success)
                {
                    long size = ParseWingetSize(output);
                    return (AvailabilityStatus.Available, size > 0 ? size : DefaultUnknownSizeMB);
                }
            }
            catch (Exception ex) { AppLogger.Write($"[AvailabilityChecker] winget show ошибка для {appId}: {ex.Message}"); }

            return (AvailabilityStatus.Unavailable, 0);
        }

        // internal, а не private: разбор зависит от культуры потока, а поймать это
        // можно только тестом, который сам подменяет CurrentCulture на ru-RU.
        internal long ParseWingetSize(string output)
        {
            try
            {
                var lines = output.Split('\n');
                foreach (var line in lines)
                {
                    if (line.Contains("Installer Size") || line.Contains("Size"))
                    {
                        var match = _wingetSizeRegex.Match(line);
                        if (match.Success)
                        {
                            // Замена запятой на точку без InvariantCulture ничего не давала:
                            // на русской локали (основная аудитория) точка не является ни
                            // десятичным разделителем, ни разделителем групп, поэтому
                            // "84.7" не разбирался вовсе — double.Parse бросал исключение,
                            // его глотал catch ниже, и размер тихо подменялся заглушкой
                            // DefaultUnknownSizeMB для каждого приложения с дробным размером.
                            // Разбор по InvariantCulture — тот же приём, что уже применён
                            // в Tools/New-CatalogDriftReport.ps1 (ConvertTo-Bytes).
                            double value = double.Parse(
                                match.Groups[1].Value.Replace(',', '.'),
                                System.Globalization.CultureInfo.InvariantCulture);
                            string unit = match.Groups[2].Value;

                            return unit switch
                            {
                                "KB" => (long)(value / 1024),
                                "MB" => (long)value,
                                "GB" => (long)(value * 1024),
                                _ => (long)value
                            };
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Ровно этот catch однажды уже спрятал культурно-зависимый сбой разбора
                // (см. комментарий выше): размер подменялся заглушкой, и в журнале не было
                // ни строки. Теперь причина видна.
                AppLogger.Write($"[AvailabilityChecker] Не удалось разобрать размер из вывода winget: {ex.Message}");
            }

            return DefaultUnknownSizeMB;
        }

        private async Task<(AvailabilityStatus Status, long SizeMB)> GetUrlInfo(string url, bool hasRegionNote)
        {
            // Тот же паритет с InstallationService/OfflineService: HTTPS-only через общий
            // DownloadValidator (не собственная копия проверки), плюс ValidateAfterRedirect —
            // редирект на небезопасный хост не должен считаться "доступно".
            if (!DownloadValidator.ValidateUrl(url))
                return (AvailabilityStatus.Unavailable, 0);

            try
            {
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_timeoutSeconds));
                using (var request = new HttpRequestMessage(HttpMethod.Head, url))
                using (var response = await _httpClient.SendAsync(request, timeoutCts.Token))
                {
                    if (response.IsSuccessStatusCode && DownloadValidator.ValidateAfterRedirect(response))
                        return ClassifySuccess(url, response);

                    if (response.StatusCode == System.Net.HttpStatusCode.MethodNotAllowed)
                    {
                        using var getCts = new CancellationTokenSource(TimeSpan.FromSeconds(_timeoutSeconds));
                        using (var getRequest = new HttpRequestMessage(HttpMethod.Get, url))
                        {
                            getRequest.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
                            using (var getResponse = await _httpClient.SendAsync(getRequest, getCts.Token))
                            {
                                if ((getResponse.IsSuccessStatusCode || getResponse.StatusCode == System.Net.HttpStatusCode.PartialContent)
                                    && DownloadValidator.ValidateAfterRedirect(getResponse))
                                    return ClassifySuccess(url, getResponse);

                                return ClassifyFailure(url, getResponse.StatusCode, hasRegionNote);
                            }
                        }
                    }

                    return ClassifyFailure(url, response.StatusCode, hasRegionNote);
                }
            }
            catch (Exception ex) { AppLogger.Write($"[AvailabilityChecker] HEAD/GET ошибка для {url}: {ex.Message}"); }

            return (AvailabilityStatus.Unavailable, 0);
        }

        // 451 (RFC 7725, Unavailable For Legal Reasons) — числовая константа, а не
        // именованный член HttpStatusCode: явное значение не зависит от того, объявлен
        // ли он в конкретной версии рантайма.
        private const System.Net.HttpStatusCode UnavailableForLegalReasonsStatusCode = (System.Net.HttpStatusCode)451;

        // Классификация неуспешного ответа — таблица из docs/superpowers/specs/
        // 2026-09-10-catalog-region-availability-design.md: 451 всегда геоблок; 403 —
        // геоблок только при сноске regionNote в каталоге (иначе неотличимо от обычной
        // защиты от ботов, см. спеку); остальное — просто недоступно. Ни одного
        // лишнего сетевого запроса: код ответа уже получен вызывающим методом.
        private static (AvailabilityStatus Status, long SizeMB) ClassifyFailure(
            string url, System.Net.HttpStatusCode statusCode, bool hasRegionNote)
        {
            if (statusCode == UnavailableForLegalReasonsStatusCode)
            {
                // M7: без regionNote в каталоге для большинства приложений это пока
                // единственный след, по которому реальный геоблок можно отличить от
                // протухшей ссылки — логируем, какое правило сработало, и по какому URL.
                AppLogger.Write($"[AvailabilityChecker] RegionBlocked (451) для {url}");
                return (AvailabilityStatus.RegionBlocked, 0);
            }

            if (statusCode == System.Net.HttpStatusCode.Forbidden && hasRegionNote)
            {
                AppLogger.Write($"[AvailabilityChecker] RegionBlocked (403 + сноска regionNote) для {url}");
                return (AvailabilityStatus.RegionBlocked, 0);
            }

            return (AvailabilityStatus.Unavailable, 0);
        }

        // Успешный (2xx) ответ — либо реальный установщик, либо CDN подменил его
        // HTML-страницей регионального ограничения после редиректа на другой хост.
        // Вердикт всё равно даёт замер: настоящий 200 с бинарным телом — Available,
        // даже если в каталоге есть сноска (сноска не отменяет успешный результат).
        private static (AvailabilityStatus Status, long SizeMB) ClassifySuccess(
            string originalUrl, HttpResponseMessage response)
        {
            if (IsGeoStubRedirect(originalUrl, response))
                return (AvailabilityStatus.RegionBlocked, 0);

            long size = SizeFromContentLength(response);
            return (AvailabilityStatus.Available, size > 0 ? size : DefaultUnknownSizeMB);
        }

        private static long SizeFromContentLength(HttpResponseMessage response) =>
            response.Content.Headers.ContentLength is { } length ? length / 1024 / 1024 : 0;

        // Эвристика "редирект на гео-заглушку": хост поменялся (значит, был редирект,
        // а не прямая отдача файла) И тело — HTML, а не бинарник. Обычный CDN-редирект
        // на зеркало с тем же типом содержимого этим условием не задевается — внешние
        // загрузки продолжают засчитываться Available, как и до этой правки. У вендоров
        // каталога нет общего фиксированного адреса геозаглушки — смена хоста плюс
        // HTML вместо бинарника единственный сигнал, который можно снять с уже
        // полученного ответа без нового сетевого запроса.
        private static bool IsGeoStubRedirect(string originalUrl, HttpResponseMessage response)
        {
            var finalUri = response.RequestMessage?.RequestUri;
            if (finalUri == null) return false;
            if (!Uri.TryCreate(originalUrl, UriKind.Absolute, out var originalUri)) return false;
            if (string.Equals(finalUri.Host, originalUri.Host, StringComparison.OrdinalIgnoreCase)) return false;

            string? mediaType = response.Content.Headers.ContentType?.MediaType;
            bool isStub = string.Equals(mediaType, "text/html", StringComparison.OrdinalIgnoreCase);
            if (isStub)
            {
                // M7: тот же мотив, что и у ClassifyFailure — без этой строки редирект
                // на гео-заглушку неотличим постфактум от обычного протухшего зеркала.
                AppLogger.Write($"[AvailabilityChecker] RegionBlocked (редирект на гео-заглушку {finalUri.Host}) для {originalUrl}");
            }
            return isStub;
        }

        // community.chocolatey.org не поддерживает HEAD (всегда 501, независимо от
        // наличия пакета — проверено вручную, тот же вывод для существующего и
        // фейкового ID). GET с Range: bytes=0-1 даёт настоящий код ответа (206 —
        // пакет есть, 404 — нет), не скачивая nupkg целиком. Тот же приём уже
        // проверен на реальных chocoId в сканере ven4admin.
        private async Task<(AvailabilityStatus Status, long SizeMB)> GetChocoPackageInfo(string chocoId)
        {
            try
            {
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_timeoutSeconds));
                string url = $"https://community.chocolatey.org/api/v2/package/{Uri.EscapeDataString(chocoId)}";
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 1);
                using var response = await _httpClient.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);

                // Размер .nupkg не отражает реальный размер устанавливаемого ПО
                // (choco-обёртка обычно в разы меньше самого инсталлятора внутри) —
                // честнее показать «размер неизвестен», чем ввести в заблуждение.
                if ((int)response.StatusCode < 400)
                    return (AvailabilityStatus.Available, DefaultUnknownSizeMB);
            }
            catch (Exception ex) { AppLogger.Write($"[AvailabilityChecker] Chocolatey-проверка ошибка для {chocoId}: {ex.Message}"); }

            return (AvailabilityStatus.Unavailable, 0);
        }

        public void ClearCache() => cache.Clear();

        public void Dispose()
        {
            // HttpClient общий (static) — живёт всё время работы приложения, не освобождается здесь.
        }
    }
}
