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

        // CDN — основной источник: быстрее GitHub и без RKN-блокировок.
        private const string CdnCatalogUrl =
            "https://cdn.ven4tools.ru/master.json";

        // GitHub raw — резервный источник, если CDN недоступен.
        private const string RemoteCatalogUrl =
            "https://raw.githubusercontent.com/Ven4ru/Ven4Tools/main/Catalog/master.json";

        // Короткий таймаут на CDN: если он не ответил быстро — не ждём, сразу пробуем GitHub.
        private const int CdnTimeoutSeconds = 4;

        private readonly string _localCatalogPath =
            Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "Data",
                "master.json");

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
                var empty = new MasterCatalog { Source = "embedded" };
                LoadedCatalog = empty;
                CatalogReady?.Invoke(empty);
                return empty;
            }

            // Источник 1 — CDN (быстрый таймаут), источник 2 — GitHub raw.
            // Первый ответивший источник выигрывает; "source" помечается соответственно.
            try
            {
                string? remoteJson = await TryDownloadAsync(CdnCatalogUrl, CdnTimeoutSeconds, ct);
                string source = "cdn";

                if (remoteJson == null)
                {
                    remoteJson = await TryDownloadAsync(RemoteCatalogUrl, _timeoutSeconds, ct);
                    source = "online";
                }

                if (remoteJson == null)
                    throw new HttpRequestException("Каталог недоступен ни с CDN, ни с GitHub");

                // Присваиваем результат до записи на диск: ошибка кэширования
                // (например, Program Files без прав на запись) не должна обнулять каталог.
                var catalog = Deserialize(remoteJson);
                catalog.Source = source;
                LoadedCatalog = catalog;
                CatalogReady?.Invoke(catalog);

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_localCatalogPath)!);
                    await File.WriteAllTextAsync(_localCatalogPath, remoteJson, ct);
                }
                catch { /* кэш — best-effort; каталог уже загружен */ }

                return catalog;
            }
            // Пробрасываем только внешнюю отмену; таймаут сети — падаем в фолбэк на кэш
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                var cached = await TryReadCacheAsync(ct);
                if (cached != null)
                {
                    LoadedCatalog = cached;
                    CatalogReady?.Invoke(cached);
                    return cached;
                }

                var empty = new MasterCatalog { Source = "embedded" };
                LoadedCatalog = empty;
                CatalogReady?.Invoke(empty);
                return empty;
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
        /// Читает кэш каталога с диска. При повреждённом JSON удаляет битый файл
        /// и возвращает null, чтобы цепочка загрузки продолжилась на embedded.
        /// </summary>
        private async Task<MasterCatalog?> TryReadCacheAsync(CancellationToken ct)
        {
            if (!File.Exists(_localCatalogPath)) return null;
            try
            {
                var json = await File.ReadAllTextAsync(_localCatalogPath, ct);
                var catalog = Deserialize(json);
                catalog.Source = "cache";
                return catalog;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
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