using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Ven4Tools.Helpers;
using Ven4Tools.Models;
using Ven4Tools.Services;

namespace Ven4Tools.ViewModels
{
    // Модель строки каталога. До перехода на MVVM (2026-07-13) CatalogTab.AppList.cs
    // строил StackPanel на каждое приложение императивно в коде — здесь то же самое
    // поведение выражено как биндящиеся свойства, а CatalogTab.xaml (DataTemplate)
    // сам решает, как их отрисовать. Это разделение позволяет добавлять новые
    // действия (см. LaunchCommand ниже) без хирургии над кодом построения UI —
    // проверено прототипом в scratch-проекте перед переносом сюда.
    public sealed class AppRowViewModel : ViewModelBase
    {
        public AppInfo App { get; }
        public string AppId => App.Id;
        public string DisplayName => App.DisplayName;
        public string CategoryString => App.CategoryString;
        public bool IsUserAdded => App.IsUserAdded;

        // Порядок вывода категорий должен быть фиксированным (как раньше — Expander'ы
        // в CatalogTab.xaml.cs шли в этом же порядке объявления), а не алфавитным.
        // AppCategory объявлен ровно в этом порядке, поэтому голое приведение к int
        // и есть нужный ранг сортировки групп.
        public int CategorySortOrder => (int)App.Category;

        // AutomationId для FlaUI-тестов (chkApp_{id}) — раньше выставлялся
        // AutomationProperties.SetAutomationId в коде, здесь тот же формат через
        // биндинг с StringFormat в CatalogTab.xaml.
        public string CheckBoxAutomationId => $"chkApp_{AppId}";

        public string? IconUrl { get; set; }

        // Описание/версия/размер из каталога (master.json) — раньше эти поля
        // JSON молча игнорировались (AppInfo их не содержит), для карточки
        // приложения нужны отдельно от IconUrl тем же способом.
        public string? Description { get; set; }

        // Идентификаторы источников установки, по которым тоже осмысленно искать.
        // В каталоге winget-идентификатор попадает в AppInfo.AlternativeId (так его
        // заполняет CatalogViewModel.SyncCatalogToAppManager из App.WingetId) —
        // отдельного поля WingetId у AppInfo нет. Свойства-обёртки нужны, чтобы
        // поиск не зависел от этой детали хранения.
        public string? WingetId => App.AlternativeId;
        public string? ChocoId => App.ChocoId;

        /// <summary>
        /// Совпадает ли строка каталога с поисковым запросом. Раньше сравнивалось только
        /// отображаемое имя, из-за чего запросы вроде «архиватор» или «блокнот» ничего не
        /// находили в курируемом каталоге, хотя эти слова есть в описании приложения, —
        /// и каталог проигрывал внешнему поиску winget/Chocolatey. Теперь запрос ищется
        /// без учёта регистра ещё и в описании, и в идентификаторах winget/Chocolatey
        /// (по ним ищут, когда знают точное имя пакета).
        /// </summary>
        public bool MatchesSearch(string? searchText)
        {
            if (string.IsNullOrWhiteSpace(searchText)) return true;

            string query = searchText.Trim();
            return ContainsQuery(DisplayName, query)
                || ContainsQuery(Description, query)
                || ContainsQuery(WingetId, query)
                || ContainsQuery(ChocoId, query);
        }

        private static bool ContainsQuery(string? value, string query) =>
            !string.IsNullOrEmpty(value) && value.Contains(query, StringComparison.OrdinalIgnoreCase);

        // Версия из каталога (последняя доступная) — отличается от InstalledVersion
        // (реально установленной), нужна для карточки, когда приложение ещё не
        // установлено.
        public string? CatalogVersion { get; set; }

        public string? CatalogSizeText { get; set; }

        // Из каталога (Models.App.Profile) — "basic"/"extended"/"full", для фильтра
        // ApplyProfileFilters. У пользовательских приложений всегда "full" (видимы
        // в любом режиме — так же вело себя вычисление profileOk=true при appId==null
        // в исходном CatalogTab.Search.cs).
        public string Profile { get; set; } = "full";

        private bool _matchesProfile = true;
        public bool MatchesProfile
        {
            get => _matchesProfile;
            set => SetField(ref _matchesProfile, value);
        }

        // Отступ строки — раньше ApplyProfileFilters выставлял его императивно на
        // каждый child (Thickness(0) для CompactMode, иначе (0,2,0,2)). Значение по
        // умолчанию читает текущий профиль сразу при создании строки, чтобы вновь
        // добавленные приложения (поиск/локальный установщик) не ждали следующего
        // ApplyProfileFilters — CatalogViewModel.ApplyProfileFilters всё равно
        // обновляет его у всех строк при смене профиля.
        private bool _isCompact = ProfileService.Current.CompactMode;
        public bool IsCompact
        {
            get => _isCompact;
            set
            {
                if (SetField(ref _isCompact, value)) OnPropertyChanged(nameof(RowMargin));
            }
        }

        public Thickness RowMargin => IsCompact ? new Thickness(0) : new Thickness(0, 2, 0, 2);

        public AppRowViewModel(AppInfo app)
        {
            App = app;
        }

        private BitmapImage? _icon;
        public BitmapImage? Icon
        {
            get => _icon;
            private set => SetField(ref _icon, value);
        }

        public async System.Threading.Tasks.Task LoadIconAsync()
        {
            if (string.IsNullOrWhiteSpace(IconUrl)) return;
            var bitmap = await IconCache.GetIconAsync(IconUrl);
            if (bitmap != null) Icon = bitmap;
        }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set { if (SetField(ref _isSelected, value)) SelectionChanged?.Invoke(); }
        }

        public event Action? SelectionChanged;

        private bool _isFavorite;
        public bool IsFavorite
        {
            get => _isFavorite;
            set
            {
                if (SetField(ref _isFavorite, value))
                {
                    OnPropertyChanged(nameof(FavoriteGlyph));
                    OnPropertyChanged(nameof(FavoriteTooltip));
                }
            }
        }

        public string FavoriteGlyph => IsFavorite ? "★" : "☆";
        // ClientUITests (Phase1DynamicButtonsTests.ЗвездаИзбранного_ПереключаетСостояние)
        // проверяет, что ToolTip реально меняется после клика — статичная строка
        // (как было в первой версии рефакторинга) ломала тест молча.
        public string FavoriteTooltip => IsFavorite ? "Убрать из избранного" : "Добавить в избранное";

        public enum RowAvailability { Checking, Available, Unavailable, Unknown }

        private RowAvailability _availability = RowAvailability.Checking;
        public RowAvailability Availability
        {
            get => _availability;
            set
            {
                if (SetField(ref _availability, value))
                {
                    OnPropertyChanged(nameof(RowBrush));
                    OnPropertyChanged(nameof(IsSelectable));
                    OnPropertyChanged(nameof(ShowSuggestButton));
                    OnPropertyChanged(nameof(StatusTooltip));
                }
            }
        }

        // JustInstalled блокирует чекбокс так же, как оригинал (IsEnabled=false после
        // успешной установки в рамках текущей сессии) — раньше строка лишь тускнела
        // (RowBrush), но оставалась выбираемой.
        public bool IsSelectable => Availability != RowAvailability.Unavailable && !JustInstalled;
        public bool ShowSuggestButton => Availability == RowAvailability.Unavailable && !IsUserAdded;

        // Скрыть можно только каталожные приложения — у пользовательских уже есть
        // свой способ убрать из списка (кнопка ❌, RemoveUserAppCommand), и это
        // необратимо, в отличие от скрытия.
        public bool ShowHideButton => !IsUserAdded;

        private bool _isInstalled;
        public bool IsInstalled
        {
            get => _isInstalled;
            set
            {
                if (SetField(ref _isInstalled, value))
                {
                    OnPropertyChanged(nameof(RowBrush));
                    OnPropertyChanged(nameof(StatusTooltip));
                }
            }
        }

        private string? _installedVersion;
        public string? InstalledVersion
        {
            get => _installedVersion;
            set { if (SetField(ref _installedVersion, value)) OnPropertyChanged(nameof(StatusTooltip)); }
        }

        private bool _hasUpdate;
        public bool HasUpdate
        {
            get => _hasUpdate;
            set
            {
                if (SetField(ref _hasUpdate, value))
                {
                    OnPropertyChanged(nameof(RowBrush));
                    OnPropertyChanged(nameof(StatusTooltip));
                }
            }
        }

        // Пользователь решил пропустить конкретное обновление (см.
        // Services/IgnoredUpdatesService.cs). Не путать с PinnedVersion ниже —
        // то выбор версии для установки прямо сейчас, а это «не напоминать
        // про эту версию». Строка при этом не красится оранжевым и получает
        // отдельный тултип, но сама возможность установить обновление остаётся.
        private bool _isUpdateIgnored;
        public bool IsUpdateIgnored
        {
            get => _isUpdateIgnored;
            set
            {
                if (SetField(ref _isUpdateIgnored, value))
                {
                    OnPropertyChanged(nameof(RowBrush));
                    OnPropertyChanged(nameof(StatusTooltip));
                    OnPropertyChanged(nameof(IgnoreUpdateGlyph));
                    OnPropertyChanged(nameof(IgnoreUpdateTooltip));
                }
            }
        }

        public string IgnoreUpdateGlyph => IsUpdateIgnored ? "🔔" : "🔕";
        public string IgnoreUpdateTooltip => IsUpdateIgnored
            ? "Показывать это обновление снова"
            : "Пропустить это обновление (до выхода следующей версии)";

        private bool _justInstalled;
        // Приложение установлено в рамках текущей сессии (только что) — тускнеет
        // и блокируется, как в реальном клиенте после успешной установки.
        public bool JustInstalled
        {
            get => _justInstalled;
            set
            {
                if (SetField(ref _justInstalled, value))
                {
                    OnPropertyChanged(nameof(RowBrush));
                    OnPropertyChanged(nameof(IsSelectable));
                }
            }
        }

        // Резервные кисти на случай, если словарь ресурсов недоступен (юнит-тесты,
        // дизайнер): те же цвета, что были зашиты в строке до перехода на темы.
        private static readonly Brush _fallbackMuted = CreateFrozen(Color.FromRgb(136, 136, 136));
        private static readonly Brush _fallbackUpdate = CreateFrozen(Color.FromRgb(255, 165, 0));
        private static readonly Brush _fallbackInstalled = CreateFrozen(Color.FromRgb(100, 149, 237));

        private static Brush CreateFrozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        // Тот же набор цветов, что в CatalogTab.Availability.cs/Install.cs — сведено
        // в одно вычисляемое свойство вместо императивной установки Foreground
        // в десятке разных мест.
        //
        // Цвета берутся из темы, а не из зашитых констант: пастельные LightGreen/
        // LightCoral на белой карточке «Светлой» темы давали контраст около 1.5:1 —
        // строка каталога буквально не читалась. Аллокации это не добавляет: кисти
        // тем заморожены в ThemeService и возвращаются по ссылке, а свойство
        // перечитывается биндингом для сотен строк при массовой проверке.
        public Brush RowBrush
        {
            get
            {
                if (JustInstalled) return BrushResolver.Resolve("TextSecondary", _fallbackMuted);
                if (IsInstalled)
                    return (HasUpdate && !IsUpdateIgnored)
                        ? BrushResolver.Resolve("StatusWarning", _fallbackUpdate)
                        : BrushResolver.Resolve("StatusInfo", _fallbackInstalled);
                return Availability switch
                {
                    RowAvailability.Available   => BrushResolver.Resolve("StatusSuccess", Brushes.LightGreen),
                    RowAvailability.Unavailable => BrushResolver.Resolve("StatusDanger", Brushes.LightCoral),
                    _                           => BrushResolver.Resolve("TextSecondary", Brushes.Gray)
                };
            }
        }

        /// <summary>
        /// Перечитать цвета строки после смены темы.
        /// <para>
        /// <see cref="RowBrush"/> — не биндинг на <c>DynamicResource</c>, а разовый
        /// снимок ресурса в момент вызова геттера, и до этого метода геттер
        /// перевызывался ТОЛЬКО при смене статуса строки (сеттеры
        /// <see cref="Availability"/>/<see cref="IsInstalled"/>/<see cref="HasUpdate"/>/
        /// <see cref="IsUpdateIgnored"/>/<see cref="JustInstalled"/> выше). Из-за этого
        /// после переключения темы на ходу все строки каталога оставались в цветах
        /// прежней темы — в «Светлой» это давало пастельные #4ADE80/#F87171 на белой
        /// карточке, контраст около 1.7-2.1:1 вместо 4.5:1, ровно тот дефект, ради
        /// которого у «Светлой» вообще свои цвета статусов.
        /// </para>
        /// <para>
        /// Строка подписывается на <c>ThemeService.ThemeChanged</c> не сама:
        /// строк семьдесят с лишним, и они пересоздаются при каждой перестройке
        /// каталога — подписка каждой на статическое событие удерживала бы их все
        /// от сборки мусора. Обходит коллекцию единственный долгоживущий владелец,
        /// <c>CatalogViewModel</c>.
        /// </para>
        /// </summary>
        public void RefreshThemeBrushes() => OnPropertyChanged(nameof(RowBrush));

        public string StatusTooltip
        {
            get
            {
                if (IsInstalled)
                {
                    if (HasUpdate && IsUpdateIgnored) return $"✓ Установлено ({InstalledVersion}) | 🔕 Обновление пропущено";
                    if (HasUpdate) return $"✓ Установлено ({InstalledVersion}) | 🆙 Доступна новая версия";
                    return string.IsNullOrEmpty(InstalledVersion) ? "✓ Уже установлено" : $"✓ Установлено ({InstalledVersion})";
                }
                return Availability switch
                {
                    RowAvailability.Available   => $"✅ Доступно для установки ({(AvailableSizeMB > 0 ? $"~{AvailableSizeMB} МБ" : "размер неизвестен")})",
                    RowAvailability.Unavailable => "❌ Недоступно",
                    // Во время ретрая проверки (только пользовательские приложения) показываем
                    // номер попытки — так же, как оригинальный CheckSingleAppAvailability.
                    // При обычной первой проверке RetryAttempt == 0 → статичный текст.
                    RowAvailability.Checking    => RetryAttempt > 0 ? $"⏳ Повторная проверка... ({RetryAttempt}/3)" : "⏳ Проверка доступности...",
                    _                           => "⚠️ Статус неизвестен"
                };
            }
        }

        // Размер загрузки (МБ) доступного приложения — заполняется проверкой
        // доступности (CheckAppAvailabilityWithSize) и подставляется в StatusTooltip,
        // как раньше делал CatalogTab.Availability.cs. 0 → «размер неизвестен».
        private long _availableSizeMB;
        public long AvailableSizeMB
        {
            get => _availableSizeMB;
            set { if (SetField(ref _availableSizeMB, value)) OnPropertyChanged(nameof(StatusTooltip)); }
        }

        // Номер текущей попытки повторной проверки доступности (1..2) — только для
        // пользовательских приложений в ретрай-цикле CatalogViewModel. 0 — обычная
        // первая проверка (без счётчика в тултипе).
        private int _retryAttempt;
        public int RetryAttempt
        {
            get => _retryAttempt;
            set { if (SetField(ref _retryAttempt, value)) OnPropertyChanged(nameof(StatusTooltip)); }
        }

        // ── Версии (пин конкретной версии вместо "Последняя") ──────────────────

        public ObservableCollection<string> VersionOptions { get; } = new() { "Последняя" };

        private string _selectedVersionOption = "Последняя";
        public string SelectedVersionOption
        {
            get => _selectedVersionOption;
            set => SetField(ref _selectedVersionOption, value);
        }

        public string? PinnedVersion => SelectedVersionOption == "Последняя" ? null : SelectedVersionOption;

        private bool _isVersionComboEnabled;
        public bool IsVersionComboEnabled
        {
            get => _isVersionComboEnabled;
            set => SetField(ref _isVersionComboEnabled, value);
        }

        public bool ShowVersionCombo => !string.IsNullOrEmpty(App.AlternativeId);

        // ── Play — резолвится только для HKLM/системных источников, см.
        // Services/AppLaunchResolver.cs. Единственная причина всего рефакторинга. ──

        private string? _launchPath;
        public string? LaunchPath
        {
            get => _launchPath;
            set { if (SetField(ref _launchPath, value)) OnPropertyChanged(nameof(CanLaunch)); }
        }

        public bool CanLaunch => IsInstalled && !string.IsNullOrEmpty(LaunchPath);

        public RelayCommand LaunchCommand => _launchCommand ??= new RelayCommand(_ => Launch());
        private RelayCommand? _launchCommand;

        private void Launch()
        {
            if (string.IsNullOrEmpty(LaunchPath)) return;
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = LaunchPath,
                    UseShellExecute = true
                });
                AppLogger.Write($"▶ Запуск {DisplayName}: {LaunchPath}");
            }
            catch (Exception ex)
            {
                AppLogger.Write($"❌ Не удалось запустить {DisplayName}: {ex.Message}");
                LaunchPath = null;
            }
        }
    }
}
