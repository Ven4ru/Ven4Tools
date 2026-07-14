using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
    public sealed class AppRowViewModel : INotifyPropertyChanged
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

        // Тот же набор цветов, что в CatalogTab.Availability.cs/Install.cs — сведено
        // в одно вычисляемое свойство вместо императивной установки Foreground
        // в десятке разных мест.
        public Brush RowBrush
        {
            get
            {
                if (JustInstalled) return new SolidColorBrush(Color.FromRgb(136, 136, 136));
                if (IsInstalled)
                    return HasUpdate
                        ? new SolidColorBrush(Color.FromRgb(255, 165, 0))
                        : new SolidColorBrush(Color.FromRgb(100, 149, 237));
                return Availability switch
                {
                    RowAvailability.Available   => Brushes.LightGreen,
                    RowAvailability.Unavailable => Brushes.LightCoral,
                    _                           => Brushes.Gray
                };
            }
        }

        public string StatusTooltip
        {
            get
            {
                if (IsInstalled)
                {
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

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }
    }
}
