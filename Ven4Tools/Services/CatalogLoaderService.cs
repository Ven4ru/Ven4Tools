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

        private readonly HttpClient _httpClient;

        private const string RemoteCatalogUrl =
            "https://raw.githubusercontent.com/Ven4ru/Ven4Tools/main/Catalog/master.json";

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
            _httpClient = new HttpClient();
            _httpClient.Timeout = Timeout.InfiniteTimeSpan;
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "Ven4Tools");
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

            try
            {
                // Таймаут per-request: связываем с внешним токеном отмены
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));

                string remoteJson =
                    await _httpClient.GetStringAsync(RemoteCatalogUrl, timeoutCts.Token);

                // Присваиваем результат до записи на диск: ошибка кэширования
                // (например, Program Files без прав на запись) не должна обнулять каталог.
                var catalog = Deserialize(remoteJson);
                catalog.Source = "online";
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
        public void Dispose() => _httpClient.Dispose();
    }
}