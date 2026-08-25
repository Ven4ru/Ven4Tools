using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using Ven4Tools.Services;

namespace Ven4Tools.ViewModels
{
    /// <summary>
    /// ViewModel вкладки «Office». Логика перенесена из code-behind при MVVM-миграции
    /// (2026-08-25, шестая вкладка после Debloater/History/About/Activation/Network)
    /// без изменения поведения — см. docs/superpowers/specs/2026-08-25-officetab-mvvm-design.md.
    /// Класс разбит на partial-файлы по образцу CatalogViewModel.Install.cs/.Presets.cs,
    /// повторяя структуру мигрируемого code-behind (Download/Install/Region).
    /// </summary>
    public sealed partial class OfficeViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        public event Action? GoToActivation;

        private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private static readonly HttpClient _httpClient = CreateHttpClient();
        private CancellationTokenSource? _cancellationTokenSource;
        private string? _downloadedFilePath;

        // Сохранённое состояние региона (Office CC и Windows GeoID)
        private string? _originalOfficeCC;
        private string? _originalGeoName;
        private string? _originalGeoNation;

        private readonly string[] officeLanguages = { "ru-ru", "en-us", "de-de", "fr-fr", "es-es", "it-it", "zh-cn", "ja-jp" };
        public string[] OfficeLanguages => officeLanguages;

        private readonly Dictionary<string, string> officeDirectLinks = new()
        {
            { "O365ProPlusRetail",       "https://c2rsetup.officeapps.live.com/c2r/download.aspx?ProductreleaseID=O365ProPlusRetail&platform=x64&language={0}&version=O16GA" },
            { "ProPlus2024Retail",       "https://c2rsetup.officeapps.live.com/c2r/download.aspx?ProductreleaseID=ProPlus2024Retail&platform=x64&language={0}&version=O16GA" },
            { "Professional2021Retail",  "https://c2rsetup.officeapps.live.com/c2r/download.aspx?ProductreleaseID=Professional2021Retail&platform=x64&language={0}&version=O16GA" },
            { "Professional2019Retail",  "https://c2rsetup.officeapps.live.com/c2r/download.aspx?ProductreleaseID=Professional2019Retail&platform=x64&language={0}&version=O16GA" },
            { "ProPlusRetail",           "https://c2rsetup.officeapps.live.com/c2r/download.aspx?ProductreleaseID=ProPlusRetail&platform=x64&language={0}&version=O16GA" }
        };

        // ── Выбор версии ─────────────────────────────────────────────────────

        private bool _isO365Selected = true;
        public bool IsO365Selected
        {
            get => _isO365Selected;
            set => SetSelectionFlag(ref _isO365Selected, value);
        }

        private bool _isO2024Selected;
        public bool IsO2024Selected
        {
            get => _isO2024Selected;
            set => SetSelectionFlag(ref _isO2024Selected, value);
        }

        private bool _isO2021Selected;
        public bool IsO2021Selected
        {
            get => _isO2021Selected;
            set => SetSelectionFlag(ref _isO2021Selected, value);
        }

        private bool _isO2019Selected;
        public bool IsO2019Selected
        {
            get => _isO2019Selected;
            set => SetSelectionFlag(ref _isO2019Selected, value);
        }

        private bool _isO2016Selected;
        public bool IsO2016Selected
        {
            get => _isO2016Selected;
            set => SetSelectionFlag(ref _isO2016Selected, value);
        }

        // Эквивалент подписки на RadioButton.Checked (не Unchecked) в оригинале —
        // инвалидация запускается только когда сеттер получает true, и только из
        // самого сеттера свойства (не из внешней подписки), поэтому начальные
        // значения, заданные field-инициализаторами выше, не могут её спровоцировать.
        private void SetSelectionFlag(ref bool field, bool value, [CallerMemberName] string? propertyName = null)
        {
            if (field == value) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            if (value) OnVersionOrLanguageChanged();
        }

        internal static (string DisplayName, string ProductId) ResolveVersion(bool o2024, bool o2021, bool o2019, bool o2016)
        {
            if (o2024) return ("Office 2024 ProPlus",     "ProPlus2024Retail");
            if (o2021) return ("Office 2021 Professional", "Professional2021Retail");
            if (o2019) return ("Office 2019 Professional", "Professional2019Retail");
            if (o2016) return ("Office 2016 Professional", "ProPlusRetail");
            return ("Office 365 ProPlus", "O365ProPlusRetail");
        }

        private (string DisplayName, string ProductId) GetSelectedVersion() =>
            ResolveVersion(IsO2024Selected, IsO2021Selected, IsO2019Selected, IsO2016Selected);

        // ── Язык интерфейса ──────────────────────────────────────────────────

        private string _selectedLanguage;
        public string SelectedLanguage
        {
            get => _selectedLanguage;
            set
            {
                if (Equals(_selectedLanguage, value)) return;
                _selectedLanguage = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedLanguage)));
                OnVersionOrLanguageChanged();
            }
        }

        // M2: смена версии/языка после скачивания должна сбрасывать уже скачанный
        // установщик — иначе «Установить» тихо поставит старую версию/язык, тогда как
        // лог/UI показывают новое выбранное значение. Проверяем именно приватное поле
        // (не HasDownloadedInstaller) — см. Global Constraints плана.
        private void OnVersionOrLanguageChanged()
        {
            if (_downloadedFilePath == null) return;

            try { if (System.IO.File.Exists(_downloadedFilePath)) System.IO.File.Delete(_downloadedFilePath); } catch { }
            _downloadedFilePath = null;
            HasDownloadedInstaller = false;
            AppLogger.Write("ℹ️ Версия/язык изменены — скачайте установщик заново");
            SetProgress(true, "ℹ️ Версия/язык изменены — скачайте установщик заново", 0, "");
        }

        // ── Сохранить установщик ────────────────────────────────────────────

        private bool _saveInstaller;
        public bool SaveInstaller
        {
            get => _saveInstaller;
            set => SetField(ref _saveInstaller, value);
        }

        // ── Состояние занятости / доступности команд ────────────────────────

        private bool _hasDownloadedInstaller;
        public bool HasDownloadedInstaller
        {
            get => _hasDownloadedInstaller;
            internal set { SetField(ref _hasDownloadedInstaller, value); InstallCommand.RaiseCanExecuteChanged(); }
        }

        private bool _isDownloading;
        public bool IsDownloading
        {
            get => _isDownloading;
            internal set { SetField(ref _isDownloading, value); RaiseAllCanExecuteChanged(); }
        }

        private bool _isInstalling;
        public bool IsInstalling
        {
            get => _isInstalling;
            internal set { SetField(ref _isInstalling, value); RaiseAllCanExecuteChanged(); }
        }

        private bool _cancelEnabled;
        public bool CancelEnabled
        {
            get => _cancelEnabled;
            internal set { SetField(ref _cancelEnabled, value); CancelCommand.RaiseCanExecuteChanged(); }
        }

        private bool _cancelVisible = true;
        public bool CancelVisible
        {
            get => _cancelVisible;
            private set => SetField(ref _cancelVisible, value);
        }

        private void RaiseAllCanExecuteChanged()
        {
            DownloadCommand.RaiseCanExecuteChanged();
            InstallCommand.RaiseCanExecuteChanged();
        }

        // ── Прогресс / статус установки ─────────────────────────────────────

        private bool _progressVisible;
        public bool ProgressVisible
        {
            get => _progressVisible;
            private set => SetField(ref _progressVisible, value);
        }

        private string _installPhaseText = "⏳ Подготовка...";
        public string InstallPhaseText
        {
            get => _installPhaseText;
            private set => SetField(ref _installPhaseText, value);
        }

        private double _progressValue;
        public double ProgressValue
        {
            get => _progressValue;
            private set => SetField(ref _progressValue, value);
        }

        private string _installDetailText = "";
        public string InstallDetailText
        {
            get => _installDetailText;
            private set => SetField(ref _installDetailText, value);
        }

        private bool _progressIndeterminate;
        public bool ProgressIndeterminate
        {
            get => _progressIndeterminate;
            private set => SetField(ref _progressIndeterminate, value);
        }

        // ── Регион ───────────────────────────────────────────────────────────

        private string _regionGeoText = "—";
        public string RegionGeoText
        {
            get => _regionGeoText;
            private set => SetField(ref _regionGeoText, value);
        }

        private string _regionCCText = "—";
        public string RegionCCText
        {
            get => _regionCCText;
            private set => SetField(ref _regionCCText, value);
        }

        // ── Подсказка активации ──────────────────────────────────────────────

        // Оригинал ставит pnlActivationHint.Visibility = Visible безусловно в
        // конструкторе поверх XAML-дефолта Collapsed — здесь это упрощено до
        // единственного значения по умолчанию (панель видна сразу и всегда,
        // как и в оригинале; отдельного сеттера не нужно — значение не меняется).
        public bool ActivationHintVisible { get; } = true;

        // ── Команды ──────────────────────────────────────────────────────────

        public RelayCommand DownloadCommand { get; }
        public RelayCommand InstallCommand { get; }
        public RelayCommand CancelCommand { get; }
        public RelayCommand GoActivationCommand { get; }

        public OfficeViewModel()
        {
            _selectedLanguage = officeLanguages[0];

            DownloadCommand     = RelayCommand.FromAsync(_ => RunDownloadAsync(), _ => !IsDownloading && !IsInstalling);
            InstallCommand      = RelayCommand.FromAsync(_ => RunInstallAsync(),  _ => HasDownloadedInstaller && !IsDownloading && !IsInstalling);
            CancelCommand       = new RelayCommand(_ => RunCancel(), _ => CancelEnabled);
            GoActivationCommand = new RelayCommand(_ => GoToActivation?.Invoke());

            // Восстановление региона после аварийного завершения (hard-kill / отключение
            // питания во время установки Office, когда finally в RunInstallAsync не успел
            // отработать). Сам маркер и восстановление живут в OfficeRegionRecoveryService —
            // вкладка создаётся лениво, привязанная к её конструктору страховка не
            // срабатывала бы там, где нужна; основной вызов — при старте клиента (App).
            // Гейт по Application.Current: восстановление пишет в реальный HKCU, а конструктор
            // VM (в отличие от прежнего конструктора OfficeTab) достижим из юнит-тестов.
            if (Application.Current != null)
            {
                RecoverRegionFromBackup();
            }

            UpdateRegionDisplay();
        }

        private void RunCancel()
        {
            _cancellationTokenSource?.Cancel();
            CancelEnabled = false;
            AppLogger.Write("⏹️ Запрос отмены...");
        }

        // ── Вспомогательные методы ────────────────────────────────────────────

        private static HttpClient CreateHttpClient()
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                UseCookies = true,
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
            };
            var client = new HttpClient(handler);
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36 Edg/120.0.0.0");
            client.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8");
            client.DefaultRequestHeaders.Add("Accept-Language", "ru-RU,ru;q=0.9,en;q=0.8");
            client.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate, br");
            client.DefaultRequestHeaders.Add("Connection", "keep-alive");
            client.DefaultRequestHeaders.Add("Upgrade-Insecure-Requests", "1");
            return client;
        }

        // `?.` — тот же паттерн, что и в UpdateRegionDisplay(): Application.Current равен
        // null и в юнит-тестах (конструктор VM достижим оттуда), и во время
        // Application.Shutdown(), а установка Office идёт до 60 минут — обновлять UI
        // на выключенном приложении не нужно и нечем.
        private void SetProgress(bool visible, string phase = "", double value = 0, string detail = "")
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                ProgressVisible    = visible;
                InstallPhaseText   = phase;
                ProgressValue      = value;
                InstallDetailText  = detail;
            });
        }

        private void SetPhase(string text) =>
            Application.Current?.Dispatcher.Invoke(() => InstallPhaseText = text);

        private void SetDetail(string text) =>
            Application.Current?.Dispatcher.Invoke(() => InstallDetailText = text);
    }
}
