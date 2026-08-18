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

        /// <summary>
        /// Показывает заглушку «Каталог недоступен» с кнопкой «Повторить загрузку».
        /// Вынесено отдельно: раньше заглушку выставляла ровно одна ветка (каталог == null),
        /// а остальные способы остаться без каталога её не показывали.
        /// </summary>
        private void ShowCatalogError(string detail)
        {
            StatusText = "❌ Ошибка: не удалось загрузить каталог";
            CatalogErrorDetail = detail;
            CatalogErrorVisible = true;
        }

        /// <param name="forceReload">
        /// Игнорировать уже загруженный в память каталог и обратиться к источникам заново.
        /// Это осознанный контракт кнопки «Повторить загрузку»: её нажимают, когда на
        /// экране висит заглушка об ошибке, и она обязана сходить к источникам в любом
        /// случае — даже если в памяти лежит пригодный каталог (например, загрузка
        /// удалась, а сорвалось уже построение списка).
        /// </param>
        public async Task LoadAsync(bool forceReload = false)
        {
            CatalogErrorVisible = false;
            StatusText = "⏳ Загрузка каталога...";
            _catalogLoader ??= new CatalogLoaderService();
            _installService ??= new InstallationService();

            try
            {
                // Состояние загрузки читается явно: переиспользуется только каталог со
                // статусом Loaded. Пустой результат прошлой попытки (LoadedEmpty) — это
                // неудача, а не результат, и подхватывать его как готовый нельзя.
                var state = forceReload ? CatalogLoadState.NotLoaded : CatalogLoaderService.State;
                if (!state.IsUsable)
                    state = CatalogLoadState.From(await _catalogLoader.LoadCatalogAsync());

                var catalog = state.Catalog;
                if (catalog == null)
                {
                    ShowCatalogError("Нет подключения к интернету или CDN недоступен.\nПроверьте сеть и нажмите «Повторить загрузку».");
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

                // Пустой каталог — такой же провал загрузки, как и null, только молчаливый.
                // LoadCatalogAsync никогда не возвращает null: без сети и кэша он отдаёт
                // встроенную копию, а если и её ресурс не читается — ПУСТОЙ каталог. Поэтому
                // ветка выше на практике недостижима, и провал распознаётся именно по статусу
                // LoadedEmpty; иначе пользователь оставался с надписью «Загружено 0 приложений»,
                // пустым списком, без заглушки и без единого способа повторить попытку. Свои
                // (пользовательские) приложения к этому моменту уже построены в BuildRows и из
                // списка не пропадают — заглушка показывается над ним.
                if (state.Status == CatalogLoadStatus.LoadedEmpty)
                    ShowCatalogError("Каталог пуст: его не удалось получить ни из сети, ни из локального кэша, ни из встроенной копии.\nПроверьте сеть и нажмите «Повторить загрузку».");

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
                // Если до построения строк дело так и не дошло — показываем ту же заглушку
                // с кнопкой повтора. Раньше исключение оставляло пользователя с пустым
                // списком и одной строкой статуса, без возможности повторить загрузку.
                // Условие по Apps.Count: если строки уже построены, а упало что-то после
                // них, список рабочий — надпись «Каталог недоступен» была бы неверной.
                if (Apps.Count == 0)
                    ShowCatalogError($"Загрузка прервана ошибкой: {ex.Message}\nНажмите «Повторить загрузку».");
            }
        }

        private async Task RefreshCatalogAsync()
        {
            Log("🔄 Обновление каталога...");
            // Заглушка снимается на время попытки: иначе после успешного «Обновить каталог»
            // на экране оставалась висеть красная плашка «Каталог недоступен» от прошлой
            // неудачи — её сбрасывала только кнопка «Повторить загрузку» (LoadAsync).
            CatalogErrorVisible = false;
            // Кеш версий winget сбрасывается только на явное «Обновить каталог» —
            // жест пользователя «дай свежее», не на каждую первичную загрузку.
            WingetVersionsService.ClearCache();
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
                if (loaded == null)
                {
                    ShowCatalogError("Нет подключения к интернету или CDN недоступен.\nПроверьте сеть и нажмите «Повторить загрузку».");
                    Log("❌ Ошибка: не удалось загрузить каталог");
                    return;
                }
                // Кэш перед обновлением удалён намеренно, поэтому пустой ответ здесь особенно
                // опасен: BuildRows очистит уже показанный список, и пользователь остался бы
                // с пустым каталогом и одной строкой в журнале. Список тогда не трогаем.
                if (loaded.Apps.Count == 0)
                {
                    ShowCatalogError("Каталог пуст: его не удалось получить ни из сети, ни из встроенной копии.\nПрежний список оставлен как есть, нажмите «Повторить загрузку».");
                    Log("❌ Ошибка: каталог получен пустым, прежний список сохранён");
                    return;
                }

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
            catch (Exception ex)
            {
                Log($"❌ Ошибка: {ex.Message}");
                if (Apps.Count == 0)
                    ShowCatalogError($"Обновление прервано ошибкой: {ex.Message}\nНажмите «Повторить загрузку».");
            }
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
            if (string.IsNullOrEmpty(size)) return (int)InstallSizeDefaults.UnknownSizeMB;
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
            catch (Exception ex)
            {
                // Ровно этот catch однажды уже спрятал культурно-зависимый сбой разбора
                // (см. комментарий выше) — размер подменялся заглушкой, и в журнале не
                // было ни строки. Тот же класс бага, что чинили в AvailabilityChecker.
                AppLogger.Write($"[CatalogViewModel] Не удалось разобрать размер «{size}»: {ex.Message}");
            }
            return (int)InstallSizeDefaults.UnknownSizeMB;
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

        /// <summary>
        /// Скрывает каталожное приложение из списка — обратимо, в отличие от
        /// RemoveUserApp выше (тот необратимо убирает пользовательское приложение).
        /// Вернуть скрытые обратно можно кнопкой «Показать скрытые» на «Настройках»
        /// (AppManager.UnhideAllApps) — здесь нет отдельного списка каждого скрытого
        /// приложения с индивидуальным «показать», только массовый сброс.
        /// </summary>
        private void HideApp(AppRowViewModel row)
        {
            _appManager.HideApp(row.AppId);
            Apps.Remove(row);
            Log($"🙈 Скрыто из каталога: {row.DisplayName} (вернуть — «Настройки» → «Показать скрытые»)");
        }
    }
}
