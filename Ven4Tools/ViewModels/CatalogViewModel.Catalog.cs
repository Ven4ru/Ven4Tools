using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Ven4Tools.Models;
using Ven4Tools.Services;

namespace Ven4Tools.ViewModels
{
    // Загрузка и обновление каталога, построение строк списка, заголовки категорий
    // и пользовательские приложения. Часть CatalogViewModel.
    public sealed partial class CatalogViewModel
    {
        // ── Загрузка каталога ───────────────────────────────────────────────────

        private string _statusText = "⏳ Загрузка каталога...";
        public string StatusText { get => _statusText; set => SetField(ref _statusText, value); }

        private bool _catalogErrorVisible;
        public bool CatalogErrorVisible { get => _catalogErrorVisible; set => SetField(ref _catalogErrorVisible, value); }

        private string _catalogErrorDetail = "";
        public string CatalogErrorDetail { get => _catalogErrorDetail; set => SetField(ref _catalogErrorDetail, value); }

        public async Task LoadAsync()
        {
            CatalogErrorVisible = false;
            StatusText = "⏳ Загрузка каталога...";
            _catalogLoader ??= new CatalogLoaderService();
            _installService ??= new InstallationService();

            try
            {
                var catalog = CatalogLoaderService.LoadedCatalog ?? await _catalogLoader.LoadCatalogAsync();
                if (catalog == null)
                {
                    StatusText = "❌ Ошибка: не удалось загрузить каталог";
                    CatalogErrorDetail = "Нет подключения к интернету или CDN недоступен.\nПроверьте сеть и нажмите «Повторить загрузку».";
                    CatalogErrorVisible = true;
                    return;
                }

                _catalog = catalog;
                SyncCatalogToAppManager();
                _appManager.LoadAlternativeSources();

                string sourceText = catalog.Source switch
                {
                    "hosting"  => "🏠 Каталог загружен с ven4tools.ru",
                    "cdn"      => "🌐 Каталог загружен с CDN (cdn.ven4tools.ru)",
                    "online"   => "🌐 Каталог загружен с GitHub",
                    "cache"    => "💾 Каталог из кэша (интернет недоступен)",
                    // Не «минимальный набор»: встроенный каталог содержит те же
                    // приложения, что и сетевой, но зафиксированные на момент сборки
                    // клиента — пользователю важна именно возможная устареваемость.
                    "embedded" => "📀 Встроенный каталог (состояние на момент сборки клиента)",
                    _          => "❓ Неизвестный источник"
                };
                Log(sourceText);
                Log($"Загружено приложений: {catalog.Apps.Count}");

                BuildRows();
                BuildCategoryHeaders();
                ApplyCategorySourceHeaders();
                ApplyProfileFilters();

                StatusText = $"Загружено {Apps.Count} приложений";
                _ = InitialLoadAvailabilityAsync().ContinueWith(
                    t => Log($"❌ Ошибка фоновой загрузки каталога: {t.Exception?.GetBaseException().Message}"),
                    TaskContinuationOptions.OnlyOnFaulted);
                _ = RefreshPresetsAsync().ContinueWith(
                    t => AppLogger.Write(t.Exception!, "Ошибка фоновой загрузки пресетов"),
                    TaskContinuationOptions.OnlyOnFaulted);
                foreach (var row in Apps)
                    _ = row.LoadIconAsync().ContinueWith(
                        t => AppLogger.Write(t.Exception!, "Ошибка загрузки иконки приложения"),
                        TaskContinuationOptions.OnlyOnFaulted);
            }
            catch (Exception ex)
            {
                StatusText = "❌ Ошибка загрузки";
                Log($"Ошибка загрузки каталога: {ex.Message}");
            }
        }

        private async Task RefreshCatalogAsync()
        {
            Log("🔄 Обновление каталога...");
            try
            {
                var catalogCachePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "master.json");
                try
                {
                    if (File.Exists(catalogCachePath)) File.Delete(catalogCachePath);
                    if (File.Exists(catalogCachePath + ".sig")) File.Delete(catalogCachePath + ".sig");
                }
                catch { }

                _catalogLoader ??= new CatalogLoaderService();
                var loaded = await _catalogLoader.LoadCatalogAsync();
                if (loaded == null) { Log("❌ Ошибка: не удалось загрузить каталог"); return; }

                _catalog = loaded;
                SyncCatalogToAppManager();
                _appManager.LoadAlternativeSources();
                _appManager.ApplyAlternativesToCatalog(_catalog);

                BuildRows();
                BuildCategoryHeaders();
                ApplyCategorySourceHeaders();
                ApplyProfileFilters();
                Log($"📦 Загружено приложений: {_catalog.Apps.Count}");
                Log("✅ Каталог успешно обновлён");

                _ = InitialLoadAvailabilityAsync().ContinueWith(
                    t => Log($"❌ Ошибка фоновой загрузки каталога: {t.Exception?.GetBaseException().Message}"),
                    TaskContinuationOptions.OnlyOnFaulted);
            }
            catch (Exception ex) { Log($"❌ Ошибка: {ex.Message}"); }
        }

        private void SyncCatalogToAppManager()
        {
            if (_catalog == null) return;
            foreach (var catalogApp in _catalog.Apps)
            {
                var existing = _appManager.GetAppById(catalogApp.Id);
                if (existing == null)
                {
                    var appInfo = new AppInfo
                    {
                        Id = catalogApp.Id,
                        DisplayName = catalogApp.Name,
                        Category = AppCategoryHelper.Parse(catalogApp.Category),
                        InstallerUrls = !string.IsNullOrEmpty(catalogApp.DownloadUrl)
                            ? new List<string> { catalogApp.DownloadUrl } : new List<string>(),
                        AlternativeId = catalogApp.WingetId,
                        RequiredSpaceMB = ParseSizeToMB(catalogApp.Size),
                        IsUserAdded = false,
                        ChocoId = catalogApp.ChocoId,
                        Sha256 = catalogApp.Sha256
                    };
                    if (!string.IsNullOrEmpty(catalogApp.SilentArgs)) appInfo.SilentArgs = catalogApp.SilentArgs;
                    _appManager.AddCatalogApp(appInfo);
                }
                else if (!existing.IsUserAdded)
                {
                    if (!string.IsNullOrEmpty(catalogApp.DownloadUrl)) existing.InstallerUrls = new List<string> { catalogApp.DownloadUrl };
                    if (!string.IsNullOrEmpty(catalogApp.WingetId)) existing.AlternativeId = catalogApp.WingetId;
                    existing.ChocoId = catalogApp.ChocoId;
                    existing.Sha256 = catalogApp.Sha256;
                    if (!string.IsNullOrEmpty(catalogApp.SilentArgs)) existing.SilentArgs = catalogApp.SilentArgs;
                }
            }
        }

        // Паттерн статический, а метод вызывается для каждого приложения при синхронизации
        // каталога — компилируем один раз вместо пересборки Regex на каждый вызов.
        private static readonly System.Text.RegularExpressions.Regex _sizeNumberRegex =
            new(@"(\d+(?:\.\d+)?)", System.Text.RegularExpressions.RegexOptions.Compiled);

        // internal, а не private: разбор зависит от культуры потока, а поймать это
        // можно только тестом, который сам подменяет CurrentCulture на ru-RU.
        internal static int ParseSizeToMB(string size)
        {
            if (string.IsNullOrEmpty(size)) return 100;
            try
            {
                var match = _sizeNumberRegex.Match(size);
                // Разбор строго по InvariantCulture: в каталоге размер всегда записан
                // с точкой ("84.7 MB"), а на русской локали точка не считается ни
                // десятичным разделителем, ни разделителем групп — TryParse по текущей
                // культуре возвращал false для 62 из 71 записи каталога, и размер молча
                // подменялся заглушкой 100 МБ. Тот же приём, что в ConvertTo-Bytes
                // из Tools/New-CatalogDriftReport.ps1 и в AvailabilityChecker.
                if (match.Success && double.TryParse(
                        match.Value,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out double value))
                    return size.Contains("GB", StringComparison.OrdinalIgnoreCase) ? (int)(value * 1024) : (int)value;
            }
            catch { }
            return 100;
        }

        private void BuildRows()
        {
            var existingUserRows = Apps.Where(a => a.IsUserAdded).ToList();
            Apps.Clear();

            if (_catalog != null)
            {
                // Целостность каталога: каталог подписан и курируется, но повреждённая
                // (хоть и валидно подписанная) запись не должна портить UI.
                //  • запись без Id или имени дала бы пустую строку-«призрак» в списке;
                //  • дублирующийся Id спроецировался бы в две одинаковые строки поверх
                //    одного и того же AppInfo (GetAppById вернул бы тот же объект).
                var seenCatalogIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var catalogApp in _catalog.Apps)
                {
                    if (string.IsNullOrWhiteSpace(catalogApp.Id) || string.IsNullOrWhiteSpace(catalogApp.Name))
                        continue;
                    if (!seenCatalogIds.Add(catalogApp.Id))
                        continue;
                    var appInfo = _appManager.GetAppById(catalogApp.Id);
                    if (appInfo == null) continue;
                    var row = new AppRowViewModel(appInfo)
                    {
                        IconUrl = catalogApp.IconUrl,
                        Profile = catalogApp.Profile,
                        Description = catalogApp.Description,
                        CatalogVersion = catalogApp.Version,
                        CatalogSizeText = catalogApp.Size
                    };
                    row.IsFavorite = _favoritesService.IsFavorite(row.AppId);
                    row.SelectionChanged += () => OnPropertyChanged(nameof(SelectedCount));
                    Apps.Add(row);
                }
            }

            foreach (var row in existingUserRows) Apps.Add(row);
            foreach (var appInfo in _appManager.GetAllApps().Where(a => a.IsUserAdded))
            {
                if (Apps.Any(a => a.AppId == appInfo.Id)) continue;
                var row = new AppRowViewModel(appInfo) { IsFavorite = _favoritesService.IsFavorite(appInfo.Id) };
                row.SelectionChanged += () => OnPropertyChanged(nameof(SelectedCount));
                Apps.Add(row);
                if (!string.IsNullOrEmpty(appInfo.AlternativeId))
                    _ = FetchVersionsForRowAsync(row).ContinueWith(
                        t => AppLogger.Write(t.Exception!, "Ошибка загрузки версий приложения"),
                        TaskContinuationOptions.OnlyOnFaulted);
            }
        }

        private void BuildCategoryHeaders()
        {
            CategoryHeaders.Clear();
            foreach (var cat in Enum.GetValues<AppCategory>())
            {
                string label = new AppInfo { Category = cat }.CategoryString;
                CategoryHeaders[label] = new CategoryHeaderViewModel(cat.ToString(), label, GetCategoryHeaderText(cat));
            }
        }

        // Соответствует GetOriginalExpanderHeader (удалённый CatalogTab.UI.cs, до
        // перехода на MVVM) — эмодзи в заголовке категории потерялись при переносе
        // на голый CategoryString.
        private static string GetCategoryHeaderText(AppCategory cat) => cat switch
        {
            AppCategory.Браузеры         => "🌐 Браузеры",
            AppCategory.Офис             => "📁 Офис",
            AppCategory.Графика          => "🎨 Графика",
            AppCategory.Разработка       => "💻 Разработка",
            AppCategory.Мессенджеры      => "💬 Мессенджеры",
            AppCategory.Мультимедиа      => "🎵 Мультимедиа",
            AppCategory.Системные        => "⚙️ Системные",
            AppCategory.ИгровыеСервисы   => "🎮 Игровые сервисы",
            AppCategory.Драйверпаки      => "🖨️ Драйверпаки",
            AppCategory.Другое           => "📎 Другое",
            AppCategory.Пользовательские => "👤 Пользовательские",
            _                            => cat.ToString()
        };

        public void ApplyCategorySourceHeaders()
        {
            bool perCategory = SourceOrderService.Current.Mode == "per_category";
            foreach (var header in CategoryHeaders.Values)
                header.ShowCombo = perCategory;
        }

        // ── Пользовательские приложения ─────────────────────────────────────────

        public void AddLocalInstallerApp(AppInfo app)
        {
            _appManager.AddUserApp(app);
            AddUserApp(app);
            Log($"📦 Локальный установщик добавлен: {app.DisplayName}");
        }

        private void AddUserApp(AppInfo app)
        {
            if (!app.IsUserAdded) _appManager.AddUserApp(app);
            var row = new AppRowViewModel(app) { IsFavorite = _favoritesService.IsFavorite(app.Id) };
            row.SelectionChanged += () => OnPropertyChanged(nameof(SelectedCount));
            Apps.Add(row);
            if (!string.IsNullOrEmpty(app.AlternativeId))
                _ = FetchVersionsForRowAsync(row).ContinueWith(
                    t => AppLogger.Write(t.Exception!, "Ошибка загрузки версий приложения"),
                    TaskContinuationOptions.OnlyOnFaulted);
        }

        private void RemoveUserApp(AppRowViewModel row)
        {
            if (MessageBox.Show("Удалить приложение из списка?", "Подтверждение",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            _appManager.RemoveUserApp(row.AppId);
            Apps.Remove(row);
            Log("🗑️ Удалено пользовательское приложение");
        }
    }
}
