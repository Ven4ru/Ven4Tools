using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Ven4Tools.Services;

namespace Ven4Tools.ViewModels
{
    public sealed partial class SystemViewModel
    {
        // Единый HttpClient для скачивания установщиков в кэш — переиспользуется,
        // чтобы не плодить сокеты (socket exhaustion) при каждом запуске загрузки.
        private static readonly HttpClient _httpClient = CreateCacheHttpClient();

        private static HttpClient CreateCacheHttpClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            client.DefaultRequestHeaders.Add("User-Agent", "Ven4Tools");
            return client;
        }

        private CancellationTokenSource? _cacheCts;
        private List<CacheAppItem> _cacheAppItems = new();

        private string _cacheStatsText = "Кэш пуст";
        public string CacheStatsText { get => _cacheStatsText; private set => SetField(ref _cacheStatsText, value); }

        private IReadOnlyList<CacheAppItem> _filteredCacheApps = Array.Empty<CacheAppItem>();
        public IReadOnlyList<CacheAppItem> FilteredCacheApps { get => _filteredCacheApps; private set => SetField(ref _filteredCacheApps, value); }

        private string _cacheAppFilterText = "";
        public string CacheAppFilterText
        {
            get => _cacheAppFilterText;
            set
            {
                if (_cacheAppFilterText == value) return;
                SetField(ref _cacheAppFilterText, value);
                ApplyCacheAppFilter();
            }
        }

        private void ApplyCacheAppFilter()
        {
            string q = CacheAppFilterText.Trim().ToLowerInvariant();
            FilteredCacheApps = string.IsNullOrEmpty(q)
                ? _cacheAppItems
                : _cacheAppItems.Where(a => a.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        private void UpdateCacheStats()
        {
            var (count, sizeMB) = OfflineService.GetCacheStats();
            CacheStatsText = count == 0
                ? "Кэш пуст"
                : $"{count} файлов · {sizeMB} МБ  ({OfflineService.CachePath})";
        }

        private void LoadCacheAppsList()
        {
            // UsableCatalog отдаёт каталог только со статусом Loaded — прежняя проверка
            // «null или пусто» теперь выражена самим состоянием загрузки.
            var catalog = CatalogLoaderService.State.UsableCatalog;
            if (catalog == null)
            {
                _cacheAppItems = new List<CacheAppItem>();
                FilteredCacheApps = _cacheAppItems;
                return;
            }

            // Кэшируются только приложения с прямой ссылкой и контрольной суммой SHA256.
            // Источник winget не поддерживает докачивание установщика в кэш, поэтому
            // winget-only приложения в этот список не попадают.
            _cacheAppItems = catalog.Apps
                .Where(a => HashHelper.HasExpectedHash(a.Sha256) &&
                            !string.IsNullOrEmpty(a.DownloadUrl))
                .OrderBy(a => a.Name)
                .Select(a => new CacheAppItem
                {
                    Id          = a.Id,
                    DisplayName = $"{a.Name}  [{a.Category}]{(OfflineService.HasCachedInstaller(a.Id) ? " ✅" : "")}",
                    DownloadUrl = a.DownloadUrl,
                    Sha256      = a.Sha256!
                })
                .ToList();

            ApplyCacheAppFilter();
        }

        private void SelectAllCache()
        {
            // L12: выбираем только видимые (не отфильтрованные поиском) элементы, а не весь
            // список — иначе «Выбрать все» тихо отмечало бы и скрытые фильтром приложения.
            foreach (var item in FilteredCacheApps) item.IsSelected = true;
        }

        private void SelectNoneCache()
        {
            foreach (var item in _cacheAppItems) item.IsSelected = false;
        }

        private void BrowseCachePath()
        {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description         = "Выберите папку для кэша установщиков",
                ShowNewFolderButton = true
            };
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

            OfflineCachePathText = dlg.SelectedPath;
            UpdateCacheStats();
        }

        private void OpenCacheFolder()
        {
            try
            {
                OfflineService.EnsureCacheDir();
                Process.Start(new ProcessStartInfo(OfflineService.CachePath) { UseShellExecute = true });
            }
            catch (Exception ex) { AppLogger.Write($"❌ {ex.Message}"); }
        }

        private void ClearCache()
        {
            var r = MessageBox.Show("Удалить все кэшированные установщики?",
                "Очистка кэша", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) return;
            OfflineService.ClearCache();
            UpdateCacheStats();
            LoadCacheAppsList();
            AppLogger.Write("✅ Кэш очищен");
        }

        private bool _isDownloadingToCache;
        public bool IsDownloadingToCache
        {
            get => _isDownloadingToCache;
            private set { SetField(ref _isDownloadingToCache, value); DownloadToCacheCommand.RaiseCanExecuteChanged(); }
        }

        private string _cacheLogText = "";
        public string CacheLogText { get => _cacheLogText; private set => SetField(ref _cacheLogText, value); }

        private void AppendCacheLog(string line)
        {
            CacheLogText += line;
            CacheLogAppended?.Invoke();
        }

        private double _cacheProgressValue;
        public double CacheProgressValue { get => _cacheProgressValue; private set => SetField(ref _cacheProgressValue, value); }

        private bool _showCacheProgress;
        public bool ShowCacheProgress { get => _showCacheProgress; private set => SetField(ref _showCacheProgress, value); }

        private bool _showCacheLog;
        public bool ShowCacheLog { get => _showCacheLog; private set => SetField(ref _showCacheLog, value); }

        private bool _showCancelCacheDownload;
        public bool ShowCancelCacheDownload { get => _showCancelCacheDownload; private set => SetField(ref _showCancelCacheDownload, value); }

        private bool _canCancelCacheDownload = true;
        public bool CanCancelCacheDownload { get => _canCancelCacheDownload; private set => SetField(ref _canCancelCacheDownload, value); }

        private async Task RunDownloadToCacheAsync()
        {
            if (IsDownloadingToCache) return;

            var selected = _cacheAppItems.Where(a => a.IsSelected).ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show("Не выбрано ни одного приложения.", "Нет выбора",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            IsDownloadingToCache = true;
            _cacheCts = new CancellationTokenSource();
            var token = _cacheCts.Token;

            ShowCancelCacheDownload = true;
            CanCancelCacheDownload  = true;
            ShowCacheProgress       = true;
            ShowCacheLog            = true;
            CacheLogText            = "";

            // Вся подготовка — внутри try: исключение здесь (например, недопустимый путь
            // кэша в EnsureCacheDir) не должно ронять команду мимо finally.
            try
            {
                SaveOfflineSettings();
                OfflineService.EnsureCacheDir();

                var http = _httpClient;
                int done = 0, total = selected.Count, errors = 0;

                foreach (var item in selected)
                {
                    if (token.IsCancellationRequested) break;

                    // Ven4Tools.Models.App — полное имя, чтобы не столкнуться с
                    // System.Windows.Application (using System.Windows уже есть в проекте).
                    var app = new Ven4Tools.Models.App
                    {
                        Id          = item.Id,
                        Name        = item.DisplayName.Split('[')[0].Trim().TrimEnd(' ', '✅').Trim(),
                        DownloadUrl = item.DownloadUrl,
                        Sha256      = item.Sha256
                    };

                    var progress = new Progress<(string status, int pct)>(v =>
                    {
                        if (v.pct >= 0) CacheProgressValue = v.pct;
                        AppendCacheLog($"[{DateTime.Now:HH:mm:ss}] {v.status}\n");
                    });

                    try
                    {
                        bool ok = await OfflineService.CacheInstallerDirectAsync(app, http, progress, token);
                        if (!ok) errors++;
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        AppendCacheLog($"❌ {app.Name}: {ex.Message}\n");
                        errors++;
                    }

                    done++;
                    CacheProgressValue = (double)done / total * 100;
                }

                string summary = token.IsCancellationRequested
                    ? $"⏹ Остановлено. Скачано: {done}/{total}"
                    : $"✅ Готово: {done}/{total}{(errors > 0 ? $", ошибок: {errors}" : "")}";
                AppendCacheLog($"\n{summary}\n");
                AppLogger.Write(summary);
            }
            catch (Exception ex)
            {
                AppendCacheLog($"❌ Ошибка: {ex.Message}\n");
                AppLogger.Write($"❌ Ошибка кэширования: {ex.Message}");
            }
            finally
            {
                IsDownloadingToCache    = false;
                ShowCancelCacheDownload = false;
                CanCancelCacheDownload  = true;
                CacheProgressValue      = 0;
                UpdateCacheStats();
                LoadCacheAppsList();

                _cacheCts.Dispose();
                _cacheCts = null;
            }
        }

        private void CancelCacheDownload()
        {
            _cacheCts?.Cancel();
            CanCancelCacheDownload = false;
        }
    }
}
