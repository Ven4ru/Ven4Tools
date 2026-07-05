using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

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
                var verified = await TryDownloadVerifiedAsync(HostingCatalogUrl, HostingTimeoutSeconds, ct);
                string source = "hosting";

                if (verified == null)
                {
                    verified = await TryDownloadVerifiedAsync(CdnCatalogUrl, CdnTimeoutSeconds, ct);
                    source = "cdn";
                }

                if (verified == null)
                {
                    verified = await TryDownloadVerifiedAsync(RemoteCatalogUrl, _timeoutSeconds, ct);
                    source = "online";
                }

                if (verified == null)
                    throw new HttpRequestException("Каталог недоступен ни с хостинга, ни с CDN, ни с GitHub");

                var (remoteJson, remoteSignature) = verified.Value;

                // Присваиваем результат до записи на диск: ошибка кэширования
                // (например, Program Files без прав на запись) не должна обнулять каталог.
                var catalog = Deserialize(remoteJson);
                catalog.Source = source;
                LoadedCatalog = catalog;
                CatalogReady?.Invoke(catalog);

                try
                {
                    // В кэш пишем ровно ту пару json+sig, которая прошла проверку.
                    // Повторное скачивание подписи создавало гонку версий: каталог на
                    // сервере мог обновиться между запросами, свежая подпись не совпадала
                    // с уже скачанным json — и кэш оставался без валидной подписи.
                    Directory.CreateDirectory(Path.GetDirectoryName(_localCatalogPath)!);
                    await File.WriteAllTextAsync(_localCatalogPath, remoteJson, ct);
                    await File.WriteAllTextAsync(LocalSignaturePath, remoteSignature, ct);
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
        private async Task<string?> TryDownloadAsync(string url, int timeoutSeconds, CancellationToken ct)
        {
            try
            {
                // Таймаут per-request: связываем с внешним токеном отмены
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));
                return await _httpClient.GetStringAsync(url, timeoutCts.Token);
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
            return JsonSerializer.Deserialize<MasterCatalog>(
                       json,
                       new JsonSerializerOptions
                       {
                           PropertyNameCaseInsensitive = true
                       })
                   ?? new MasterCatalog();
        }
        // HttpClient общий (static) — живёт всё время работы приложения, не освобождается здесь.
        public void Dispose() { }
    }
}
