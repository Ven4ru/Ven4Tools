using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Newtonsoft.Json;
using Ven4Tools.Helpers;
using Ven4Tools.Models;

namespace Ven4Tools.Services
{
    public class CatalogLoaderService : IDisposable
    {
        public static MasterCatalog? LoadedCatalog { get; private set; }
        public static event Action<MasterCatalog>? CatalogReady;

        private static readonly SemaphoreSlim _preloadLock = new(1, 1);

        // Один общий HttpClient на приложение: пересоздание на каждый инстанс
        // приводит к socket exhaustion (рекомендация MS).
        private static readonly HttpClient _httpClient = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestHeaders = { { "User-Agent", "Ven4Tools" } }
        };

        // Хостинг — первый источник: минимальный RTT (тот же сервер, что и API).
        private const string HostingCatalogUrl =
            "https://ven4tools.ru/catalog/master.json";

        // CDN — второй источник: быстрее GitHub и без RKN-блокировок.
        private const string CdnCatalogUrl =
            "https://cdn.ven4tools.ru/master.json";

        // GitHub raw — резервный источник, если хостинг и CDN недоступны.
        private const string RemoteCatalogUrl =
            "https://raw.githubusercontent.com/Ven4ru/Ven4Tools/main/Catalog/master.json";

        // Короткий таймаут на хостинг: если не ответил быстро — сразу пробуем CDN.
        private const int HostingTimeoutSeconds = 3;

        // Таймаут на CDN: если не ответил — переходим на GitHub.
        private const int CdnTimeoutSeconds = 4;

        private readonly string _localCatalogPath =
            Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "Data",
                "master.json");
        private string LocalSignaturePath => _localCatalogPath + ".sig";

        // Таймаут хранится отдельно и применяется per-request через CancellationTokenSource:
        // менять HttpClient.Timeout после первого запроса нельзя (InvalidOperationException)
        private volatile int _timeoutSeconds;

        public CatalogLoaderService()
        {
            _timeoutSeconds = Math.Max(3, AppSettings.CatalogTimeout);
        }

        public void UpdateTimeout(int seconds)
        {
            _timeoutSeconds = Math.Max(3, seconds);
        }

        public async Task<MasterCatalog> LoadCatalogAsync(CancellationToken ct = default)
        {
            // Offline mode — skip remote, use local cache or embedded
            if (ProfileService.Current.OfflineMode)
            {
                var cached = await TryReadCacheAsync(ct);
                if (cached != null)
                {
                    LoadedCatalog = cached;
                    CatalogReady?.Invoke(cached);
                    return cached;
                }
                var embedded = LoadEmbeddedCatalog();
                LoadedCatalog = embedded;
                CatalogReady?.Invoke(embedded);
                return embedded;
            }

            // Источник 1 — хостинг (3s), источник 2 — CDN (4s), источник 3 — GitHub raw.
            // Первый ответивший источник выигрывает; "source" помечается соответственно.
            // Подпись обязательна для КАЖДОГО источника (fail-closed): источник без
            // валидной подписи пропускается, каталог без подписи не принимается.
            try
            {
                // Каждый источник проверяется целиком: подпись + защита от отката версии.
                // Источник с валидной подписью, но версией ниже последней применённой
                // (downgrade-атака) отвергается так же, как источник без подписи — и
                // цепочка продолжается на следующий источник (fail-closed).
                var loaded =
                    await TryLoadFromSourceAsync(HostingCatalogUrl, HostingTimeoutSeconds, "hosting", ct)
                    ?? await TryLoadFromSourceAsync(CdnCatalogUrl, CdnTimeoutSeconds, "cdn", ct)
                    ?? await TryLoadFromSourceAsync(RemoteCatalogUrl, _timeoutSeconds, "online", ct);

                if (loaded == null)
                    throw new HttpRequestException("Каталог недоступен ни с хостинга, ни с CDN, ни с GitHub");

                var (catalog, remoteJson, remoteSignature) = loaded.Value;

                // Присваиваем результат до записи на диск: ошибка кэширования
                // (например, Program Files без прав на запись) не должна обнулять каталог.
                LoadedCatalog = catalog;
                CatalogReady?.Invoke(catalog);
                RememberCatalogVersion(catalog.Version);

                try
                {
                    // В кэш пишем ровно ту пару json+sig, которая прошла проверку.
                    // Повторное скачивание подписи создавало гонку версий: каталог на
                    // сервере мог обновиться между запросами, свежая подпись не совпадала
                    // с уже скачанным json — и кэш оставался без валидной подписи.
                    // Каждый файл пишется атомарно (temp+rename) — обрыв посреди записи
                    // не оставляет битый/усечённый json или sig на диске. Окно рассинхрона
                    // между json и sig (если процесс убьют между двумя rename) остаётся, но
                    // fail-closed проверка подписи при чтении кэша отклонит несовпавшую пару.
                    await FileHelper.WriteAllTextAtomicAsync(_localCatalogPath, remoteJson);
                    await FileHelper.WriteAllTextAtomicAsync(LocalSignaturePath, remoteSignature);
                }
                catch { /* кэш — best-effort; каталог уже загружен */ }

                return catalog;
            }
            // Пробрасываем только внешнюю отмену; таймаут сети — падаем в фолбэк на кэш
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[CatalogLoaderService] Не удалось загрузить каталог из сети — переход на кэш/встроенный: {ex.Message}");
                var cached = await TryReadCacheAsync(ct);
                if (cached != null)
                {
                    LoadedCatalog = cached;
                    CatalogReady?.Invoke(cached);
                    return cached;
                }

                var embedded = LoadEmbeddedCatalog();
                LoadedCatalog = embedded;
                CatalogReady?.Invoke(embedded);
                return embedded;
            }
        }

        /// <summary>
        /// Скачивает каталог с указанного URL с заданным таймаутом.
        /// Возвращает null при любой сетевой ошибке/таймауте источника — чтобы
        /// вызывающий код продолжил цепочку (CDN → GitHub). Внешняя отмена пробрасывается.
        /// </summary>
        // Каталог реально весит десятки-сотни КБ; лимит с большим запасом, но
        // конечный — без него ответ буферизуется в память целиком ДО проверки
        // ECDSA-подписи (GetStringAsync не ограничен), что даёт скомпрометированному
        // или MITM-источнику устроить OOM ещё до того, как подпись успеет отклонить подделку.
        private const long MaxCatalogResponseBytes = 16 * 1024 * 1024; // 16 МБ

        private async Task<string?> TryDownloadAsync(string url, int timeoutSeconds, CancellationToken ct)
        {
            try
            {
                // Таймаут per-request: связываем с внешним токеном отмены
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));

                using var response = await _httpClient.GetAsync(
                    url, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
                response.EnsureSuccessStatusCode();

                if (response.Content.Headers.ContentLength is { } declared &&
                    declared > MaxCatalogResponseBytes)
                    return null;

                await using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token);
                using var ms = new System.IO.MemoryStream();
                var buffer = new byte[81920];
                int read;
                long total = 0;
                while ((read = await stream.ReadAsync(buffer, timeoutCts.Token)) > 0)
                {
                    total += read;
                    if (total > MaxCatalogResponseBytes) return null;
                    await ms.WriteAsync(buffer.AsMemory(0, read), timeoutCts.Token);
                }
                return System.Text.Encoding.UTF8.GetString(ms.ToArray());
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Скачивает каталог и его подпись, проверяет пару и возвращает её целиком.
        /// Проверка строгая (fail-closed): нет подписи или подпись невалидна —
        /// источник отклоняется (null), «мягкого» режима без подписи не существует.
        /// Возврат пары позволяет закэшировать именно проверенные json+sig,
        /// не скачивая подпись повторно.
        /// </summary>
        private async Task<(string Json, string Signature)?> TryDownloadVerifiedAsync(
            string url, int timeoutSeconds, CancellationToken ct)
        {
            var json = await TryDownloadAsync(url, timeoutSeconds, ct);
            if (json == null) return null;
            var signature = await TryDownloadAsync(url + ".sig", timeoutSeconds, ct);
            if (signature == null || !CatalogSignatureVerifier.Verify(json, signature))
            {
                AppLogger.Write($"[CatalogLoaderService] Подпись каталога недействительна: {url}");
                return null;
            }
            return (json, signature);
        }

        /// <summary>
        /// Полная загрузка каталога из одного источника: скачивание + проверка подписи
        /// (fail-closed) + защита от отката версии. Возвращает готовый каталог вместе с
        /// проверенной парой json+sig (для кэша) либо null, если источник недоступен,
        /// подпись невалидна или версия ниже последней применённой (downgrade).
        /// </summary>
        private async Task<(MasterCatalog Catalog, string Json, string Signature)?> TryLoadFromSourceAsync(
            string url, int timeoutSeconds, string source, CancellationToken ct)
        {
            var verified = await TryDownloadVerifiedAsync(url, timeoutSeconds, ct);
            if (verified == null) return null;

            var (json, signature) = verified.Value;
            var catalog = Deserialize(json);

            if (!IsCatalogVersionAcceptable(catalog.Version, source))
                return null;

            catalog.Source = source;
            return (catalog, json, signature);
        }

        /// <summary>
        /// Проверка защиты от отката: версия каталога не должна быть СТРОГО меньше
        /// последней успешно применённой (запомненной в профиле). Равная версия
        /// принимается (переустановка той же версии), 0 в памяти означает первый
        /// запуск — принимается любая версия.
        /// </summary>
        private static bool IsCatalogVersionAcceptable(int candidateVersion, string source)
        {
            int lastSeen = CatalogVersionGuard.Load();
            bool acceptable = IsVersionAcceptable(candidateVersion, lastSeen);
            if (!acceptable)
            {
                AppLogger.Write(
                    $"[CatalogLoaderService] Отклонён каталог версии {candidateVersion} < последней применённой {lastSeen} " +
                    $"(защита от отката), источник: {source}");
            }
            return acceptable;
        }

        /// <summary>
        /// Чистое правило защиты от отката (без побочных эффектов, для тестов):
        /// версия приемлема, если памяти о версии ещё нет (lastSeen ≤ 0, первый запуск)
        /// либо версия не меньше последней применённой.
        /// </summary>
        internal static bool IsVersionAcceptable(int candidateVersion, int lastSeenVersion)
        {
            if (lastSeenVersion <= 0) return true;
            return candidateVersion >= lastSeenVersion;
        }

        /// <summary>
        /// Запоминает версию успешно применённого каталога, если она выше запомненной.
        /// Значение хранится в DPAPI-защищённом CatalogVersionGuard (а не в plaintext
        /// profile.json), чтобы счётчик anti-rollback нельзя было сбросить правкой файла.
        /// Best-effort: сбой записи не должен ломать загрузку каталога (Save сам no-op,
        /// если версия не выше сохранённой).
        /// </summary>
        private static void RememberCatalogVersion(int version)
        {
            try
            {
                CatalogVersionGuard.Save(version);
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[CatalogLoaderService] Не удалось запомнить версию каталога: {ex.Message}");
            }
        }

        /// <summary>
        /// Читает кэш каталога с диска. Кэш тоже строго fail-closed: без файла
        /// подписи или с невалидной подписью возвращается null. При повреждённом
        /// JSON удаляет битый файл, чтобы цепочка загрузки продолжилась на embedded.
        /// </summary>
        private async Task<MasterCatalog?> TryReadCacheAsync(CancellationToken ct)
        {
            if (!File.Exists(_localCatalogPath)) return null;
            try
            {
                var json = await File.ReadAllTextAsync(_localCatalogPath, ct);
                if (!File.Exists(LocalSignaturePath)) return null;
                var signature = await File.ReadAllTextAsync(LocalSignaturePath, ct);
                if (!CatalogSignatureVerifier.Verify(json, signature)) return null;
                var catalog = Deserialize(json);
                // Кэш в переносимом/непривилегированном режиме лежит в user-writable
                // папке — применяем ту же защиту от отката, что и к сетевым источникам.
                if (!IsCatalogVersionAcceptable(catalog.Version, "cache")) return null;
                catalog.Source = "cache";
                return catalog;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[CatalogLoaderService] Повреждён кэш каталога — файл удалён: {ex.Message}");
                // Битый кэш — удаляем, чтобы при следующем запуске не споткнуться снова
                try { File.Delete(_localCatalogPath); } catch { }
                return null;
            }
        }

        public static async Task PreloadAsync(CancellationToken ct = default)
        {
            if (LoadedCatalog != null) return;

            // Только один preload одновременно — иначе параллельные вызовы
            // (splash + фоновые тригеры) дублируют сетевые запросы и гонку за LoadedCatalog.
            await _preloadLock.WaitAsync(ct);
            try
            {
                if (LoadedCatalog != null) return;

                using var loader = new CatalogLoaderService();
                await loader.LoadCatalogAsync(ct);
            }
            finally
            {
                _preloadLock.Release();
            }
        }

        /// <summary>
        /// Читает встроенный каталог (Resources/embedded_catalog.json) — последний
        /// резерв, когда сеть и дисковый кэш недоступны. Даёт офлайн-установку с
        /// реальным набором приложений вместо пустого каталога. При отсутствии/ошибке
        /// ресурса возвращает пустой каталог, чтобы приложение не падало.
        /// </summary>
        private MasterCatalog LoadEmbeddedCatalog()
        {
            try
            {
                var asm = typeof(CatalogLoaderService).Assembly;
                using var stream = asm.GetManifestResourceStream("Ven4Tools.Resources.embedded_catalog.json");
                if (stream != null)
                {
                    using var reader = new StreamReader(stream);
                    var json = reader.ReadToEnd();
                    var catalog = Deserialize(json);
                    catalog.Source = "embedded";
                    return catalog;
                }
                AppLogger.Write("[CatalogLoaderService] Встроенный каталог не найден среди ресурсов сборки");
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[CatalogLoaderService] Не удалось прочитать встроенный каталог: {ex.Message}");
            }
            return new MasterCatalog { Source = "embedded" };
        }

        private MasterCatalog Deserialize(string json)
        {
            // Newtonsoft сопоставляет имена свойств без учёта регистра по умолчанию —
            // прежний PropertyNameCaseInsensitive из System.Text.Json не нужен.
            return JsonConvert.DeserializeObject<MasterCatalog>(json)
                   ?? new MasterCatalog();
        }
        // HttpClient общий (static) — живёт всё время работы приложения, не освобождается здесь.
        public void Dispose() { }
    }
}
