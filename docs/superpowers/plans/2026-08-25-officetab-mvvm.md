# OfficeTab MVVM Migration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Перенести логику вкладки «Office» (`OfficeTab`, 686 строк в 4 partial-файлах code-behind) из code-behind в `OfficeViewModel`, оставив `OfficeTab.xaml`/`.xaml.cs` тонкой обёрткой. Шестая вкладка серии MVVM-миграции, самая рискованная на сегодня (реальное скачивание, elevated-установка, смена региона реестра).

**Architecture:** `OfficeViewModel : INotifyPropertyChanged`, partial-класс по образцу `CatalogViewModel.Install.cs`/`.Presets.cs` — `OfficeViewModel.cs` (ядро), `OfficeViewModel.Download.cs`, `OfficeViewModel.Install.cs`, `OfficeViewModel.Region.cs`, та же файловая структура, что у мигрируемого code-behind. Команды — `RelayCommand`/`RelayCommand.FromAsync`.

**Tech Stack:** .NET 8, WPF, xUnit.

## Global Constraints

- Поведение 1:1 с оригиналом, кроме трёх явных механических адаптаций:
  1. `this.Dispatcher.Invoke(...)` → `System.Windows.Application.Current.Dispatcher.Invoke(...)`.
  2. `Views.UiGuards.WarnIfInstallBusy()`/`InstallationService.InstallSemaphore` вызываются из VM напрямую (уже устоявшийся паттерн — `HistoryViewModel`, `CatalogViewModel.Install.cs`, `AppCardViewModel`).
  3. `event Action? GoToActivation` остаётся публичным на `OfficeTab` (UserControl) — `MainWindow.xaml.cs:250` подписывается на него напрямую. VM получает свой `event Action? GoToActivation`, code-behind ретранслирует его в свой.
- **Гейт реентерабельности** (урок NetworkTab — `CanExecute`/`CommandManager` асинхронны, между `IsBusy=true` и визуальным disable есть Background-priority зазор): `RunDownloadAsync`/`RunInstallAsync` начинаются с явного `if (IsDownloading || IsInstalling) return;` первой строкой, ДО любой другой логики.
- `OnVersionOrLanguageChanged()` (инвалидация скачанного файла при смене версии/языка) обязана проверять приватное поле `_downloadedFilePath == null`, НЕ публичное свойство `HasDownloadedInstaller` — иначе юнит-тесты, меняющие `IsOXxxSelected`/`SelectedLanguage` на VM без `Application.Current`, упадут при попытке дойти до `SetProgress` → `Application.Current.Dispatcher`.
- `HasDownloadedInstaller`/`IsDownloading`/`IsInstalling`/`CancelEnabled` — сеттеры `internal` (не `private`) для тестируемости `CanExecute`-переходов; `InternalsVisibleTo("Ven4Tools.Tests")` уже объявлен в `Ven4Tools/Properties/AssemblyInfo.cs`.
- `CancelCommand.CanExecute(null)` обязан быть `false` по умолчанию — это прямо проверяется существующим UI-тестом `OfficeTab_ОтменаИПереходКАктивации` (`Ven4Tools.ClientUITests/Phase3RemainingTabsTests.cs:226`).
- Все `x:Name`, участвующие в тестах, сохраняются дословно: `btnDownloadOffice`, `btnCancelOffice`, `btnGoActivation`.
- Никакой статический `IsEnabled` на кнопках не нужен — `CanExecute` + `CommandManager` (подтверждённый вывод предыдущих 5 вкладок).
- Коммиты — на русском, без Claude/AI-атрибуции.
- Ветка `mvvm-officetab` уже создана от `main`, спека закоммичена (`cbd7602`).

---

### Task 1: `OfficeViewModel` (4 partial-файла) + юнит-тесты

**Files:**
- Create: `Ven4Tools/ViewModels/OfficeViewModel.cs`
- Create: `Ven4Tools/ViewModels/OfficeViewModel.Download.cs`
- Create: `Ven4Tools/ViewModels/OfficeViewModel.Install.cs`
- Create: `Ven4Tools/ViewModels/OfficeViewModel.Region.cs`
- Test: `tests/Ven4Tools.Tests/OfficeViewModelTests.cs`

**Interfaces:**
- Consumes: `Ven4Tools.Services.AppLogger.Write`, `Ven4Tools.Services.InstallationService.InstallSemaphore`, `Ven4Tools.Services.OfficeRegionRecoveryService` (`Save`/`Recover`/`Delete`), `Ven4Tools.Views.UiGuards.WarnIfInstallBusy()`, `Ven4Tools.Shared.AuthenticodeVerifier.IsSignedByMicrosoft`, `Microsoft.Win32.Registry`, `Ven4Tools.ViewModels.RelayCommand`/`RelayCommand.FromAsync`.
- Produces: `Ven4Tools.ViewModels.OfficeViewModel` — публичные свойства `IsO365Selected`/`IsO2024Selected`/`IsO2021Selected`/`IsO2019Selected`/`IsO2016Selected`, `OfficeLanguages`, `SelectedLanguage`, `SaveInstaller`, `HasDownloadedInstaller`, `IsDownloading`, `IsInstalling`, `CancelEnabled`, `CancelVisible`, `ProgressVisible`, `InstallPhaseText`, `ProgressValue`, `InstallDetailText`, `ProgressIndeterminate`, `RegionGeoText`, `RegionCCText`, `ActivationHintVisible`; команды `DownloadCommand`, `InstallCommand`, `CancelCommand`, `GoActivationCommand`; событие `GoToActivation`; `internal static (string DisplayName, string ProductId) ResolveVersion(bool o2024, bool o2021, bool o2019, bool o2016)`.

- [ ] **Step 1: Создать `Ven4Tools/ViewModels/OfficeViewModel.cs`**

Полное содержимое файла:

```csharp
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
            RecoverRegionFromBackup();
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

        private void SetProgress(bool visible, string phase = "", double value = 0, string detail = "")
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                ProgressVisible    = visible;
                InstallPhaseText   = phase;
                ProgressValue      = value;
                InstallDetailText  = detail;
            });
        }

        private void SetPhase(string text) =>
            Application.Current.Dispatcher.Invoke(() => InstallPhaseText = text);

        private void SetDetail(string text) =>
            Application.Current.Dispatcher.Invoke(() => InstallDetailText = text);
    }
}
```

- [ ] **Step 2: Создать `Ven4Tools/ViewModels/OfficeViewModel.Download.cs`**

Полное содержимое файла:

```csharp
using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Ven4Tools.Services;

namespace Ven4Tools.ViewModels
{
    public sealed partial class OfficeViewModel
    {
        // ── Скачивание ────────────────────────────────────────────────────────

        private async Task RunDownloadAsync()
        {
            if (IsDownloading || IsInstalling) return;
            if (SelectedLanguage == null) return;

            var (displayName, productId) = GetSelectedVersion();
            string lang = SelectedLanguage;

            // Удаляем предыдущий скачанный установщик, если он остался
            if (_downloadedFilePath != null)
            {
                try { File.Delete(_downloadedFilePath); } catch { }
                _downloadedFilePath = null;
            }

            IsDownloading = true;
            HasDownloadedInstaller = false;
            CancelEnabled = true;
            CancelVisible = true;

            _cancellationTokenSource = new CancellationTokenSource();
            var token = _cancellationTokenSource.Token;

            SetProgress(true, "⏳ Подготовка...", 0, "");
            AppLogger.Write($"\n📥 Скачивание {displayName} ({lang})...");

            string tempFile = Path.Combine(Path.GetTempPath(), $"OfficeSetup_{Guid.NewGuid():N}.exe");

            try
            {
                string downloadUrl = string.Format(officeDirectLinks[productId], lang);
                // Таймаут 30 секунд на соединение и заголовки — вторая половина той же
                // пары, что и sliding-таймаут ниже (см. InstallationService.DirectDownload,
                // где пара введена целиком). Без него фаза заголовков ограничена только
                // общим таймаутом HttpClient по умолчанию, то есть кнопка «Скачать»
                // остаётся заблокированной сильно дольше нужного на молчащем сервере.
                using var headersCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                headersCts.CancelAfter(TimeSpan.FromSeconds(30));
                using var response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, headersCts.Token);
                response.EnsureSuccessStatusCode();

                using var src = await response.Content.ReadAsStreamAsync(token);
                using var dst = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None);
                var  buf      = new byte[65536];
                int  read;
                long total    = 0;
                long? size    = response.Content.Headers.ContentLength;
                int  lastPct  = -1;

                // Sliding-таймаут простоя между чтениями — тот же класс риска, что и
                // в InstallationService/FallbackDownloader/OfflineService: зависший или
                // крайне медленный сервер иначе вешал бы загрузку до ручной отмены.
                using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                idleCts.CancelAfter(TimeSpan.FromSeconds(60));

                while ((read = await src.ReadAsync(buf, idleCts.Token)) > 0)
                {
                    idleCts.CancelAfter(TimeSpan.FromSeconds(60));
                    await dst.WriteAsync(buf, 0, read, token);
                    total += read;

                    if (size.HasValue)
                    {
                        int pct = (int)(total * 100.0 / size.Value);
                        if (pct != lastPct)
                        {
                            lastPct = pct;
                            SetProgress(true,
                                $"📥 Скачивание: {pct}%", pct,
                                $"{(double)total / 1_048_576:F1} / {(double)size.Value / 1_048_576:F1} МБ");
                        }
                    }
                    else
                    {
                        SetProgress(true, "📥 Скачивание...", 0,
                            $"{(double)total / 1_048_576:F1} МБ");
                    }
                }

                var fi = new FileInfo(tempFile);
                AppLogger.Write($"✅ Скачано: {fi.Length / 1_048_576.0:F1} МБ");
                SetProgress(true, "✅ Скачано! Нажмите «Установить»", 100,
                    $"{fi.Length / 1_048_576.0:F1} МБ");

                _downloadedFilePath = tempFile;
                HasDownloadedInstaller = true;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Idle-таймаут (не token) падает в общий catch ниже — показывается как
                // обычная ошибка загрузки, а не как «отменено пользователем».
                AppLogger.Write("⏹️ Скачивание отменено");
                SetProgress(true, "⏹️ Отменено", 0, "");
                try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
            }
            catch (Exception ex)
            {
                AppLogger.Write($"❌ Ошибка скачивания: {ex.Message}");
                SetProgress(true, "❌ Ошибка", 0, "");
                try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
                MessageBox.Show("Не удалось скачать Office. Проверьте подключение к интернету и попробуйте ещё раз.",
                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
                Application.Current.Dispatcher.Invoke(() =>
                {
                    IsDownloading = false;
                    CancelEnabled = false;
                });
            }
        }
    }
}
```

- [ ] **Step 3: Создать `Ven4Tools/ViewModels/OfficeViewModel.Install.cs`**

Полное содержимое файла:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Ven4Tools.Services;
using Ven4Tools.Shared;

namespace Ven4Tools.ViewModels
{
    public sealed partial class OfficeViewModel
    {
        // ── Установка ─────────────────────────────────────────────────────────

        private async Task RunInstallAsync()
        {
            if (IsInstalling || IsDownloading) return;

            if (_downloadedFilePath == null || !File.Exists(_downloadedFilePath))
            {
                AppLogger.Write("⚠️ Файл установщика не найден — скачайте снова.");
                HasDownloadedInstaller = false;
                return;
            }

            // Установка Office — такая же длительная установка с повышением прав, как
            // и установка приложений каталога, поэтому она обязана делить общий признак
            // занятости: иначе каталог считает, что установок нет, и запускает вторую
            // параллельно (два установщика Windows одновременно + переключение региона
            // системы у этой вкладки), а шапка показывает «Нет активных задач».
            if (Views.UiGuards.WarnIfInstallBusy()) return;

            string installerPath = _downloadedFilePath;

            var (displayName, _) = GetSelectedVersion();

            IsInstalling = true;
            HasDownloadedInstaller = false;
            CancelEnabled = true;
            CancelVisible = true;

            _cancellationTokenSource = new CancellationTokenSource();
            var token = _cancellationTokenSource.Token;
            bool regionChanged = false;

            SetProgress(true, "⏳ Подготовка установки...", 0, "");
            AppLogger.Write($"\n🚀 Установка {displayName}...");

            await InstallationService.InstallSemaphore.WaitAsync();
            try
            {
                SetPhase("🔐 Проверка подлинности установщика...");

                // FileShare.Read держим открытым от проверки подписи до запуска
                // установщика — запрещает подмену файла другим процессом того же
                // пользователя в этом окне (TOCTOU), как в MainWindow.Components.cs
                // (InstallWebView2Async/InstallVcRedistAsync лаунчера). Хендл
                // закрывается явно (не using var на весь блок), чтобы не держать
                // файл заблокированным для удаления в ветке отказа проверки ниже.
                var installerHandle = new FileStream(installerPath, FileMode.Open, FileAccess.Read, FileShare.Read);

                if (!AuthenticodeVerifier.IsSignedByMicrosoft(installerPath, out string signatureError))
                {
                    installerHandle.Dispose();
                    AppLogger.Write("❌ Не удалось подтвердить подлинность установщика Microsoft — скачайте заново");
                    AppLogger.Write($"   Причина: {signatureError}");
                    TryDeleteDownloadedInstaller();
                    SetProgress(true, "❌ Подлинность не подтверждена", 0, "Скачайте установщик заново.");
                    MessageBox.Show("Не удалось подтвердить подлинность установщика Microsoft — скачайте заново.",
                        "Проверка установщика", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                AppLogger.Write("✅ Подпись установщика Microsoft подтверждена");

                SaveRegion();
                regionChanged = true; // до SetRegionUS — чтобы finally откатил даже при исключении внутри
                SetRegionUS();
                AppLogger.Write("🌎 Регион переключён на US (GeoID: 244, CountryCode: US)");

                SetPhase("🚀 Запуск установщика...");
                var existingPids = GetC2RProcessPids();

                // Последняя точка, где отмена ещё безопасна: если пользователь нажал
                // «Отмена» на этапе проверки подписи — прерываемся ДО запуска установщика
                // (регион восстановит finally). После Process.Start отмена уже недоступна.
                token.ThrowIfCancellationRequested();

                using var bootstrapper = System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo
                    {
                        FileName        = installerPath,
                        UseShellExecute = true,
                        Verb            = "runas"
                    });
                // ShellExecuteEx уже открыл/запустил файл к моменту возврата
                // из Process.Start — хендл-защита от подмены больше не нужна.
                installerHandle.Dispose();

                if (bootstrapper != null)
                {
                    // M3: elevated-процесс установщика уже запущен — реальную установку
                    // отменить нельзя (регион будет восстановлен только после её завершения).
                    // Прячем «Отмена», чтобы UI не обещал невозможного.
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        CancelEnabled = false;
                        CancelVisible = false;
                    });
                    SetPhase("⚙️ Установка Office запущена — отменить нельзя, дождитесь завершения");

                    await bootstrapper.WaitForExitAsync(token);
                    if (bootstrapper.ExitCode != 0)
                    {
                        AppLogger.Write($"❌ Установщик завершился с кодом {bootstrapper.ExitCode}");
                        AppLogger.Write("   Вероятная причина: CDN Microsoft заблокирован в вашем регионе.");
                        AppLogger.Write("   Попробуйте использовать VPN и повторить установку.");
                        SetProgress(true, $"❌ Сбой установки (код {bootstrapper.ExitCode})", 0,
                            "CDN Microsoft может быть недоступен. Попробуйте VPN.");
                        return;
                    }
                }

                token.ThrowIfCancellationRequested();

                SetPhase("⚙️ Установка Office... не закрывайте приложение");
                AppLogger.Write("⏳ Ожидаем запуск C2R-установщика...");

                using var installProc = await WaitForC2RProcess(existingPids, TimeSpan.FromMinutes(3), token);

                if (installProc == null)
                {
                    AppLogger.Write("⚠️ Процесс установки не обнаружен — возможно Office уже установлен или завершился мгновенно");
                }
                else
                {
                    AppLogger.Write($"🔍 Мониторинг: {installProc.ProcessName} (PID {installProc.Id})");
                    SetProgress(true, "⚙️ Установка Office...", 0, "Идёт установка, пожалуйста подождите...");
                    ProgressIndeterminate = true;
                    await MonitorInstallation(installProc, token);
                    ProgressIndeterminate = false;
                }

                token.ThrowIfCancellationRequested();

                RestoreRegion();
                regionChanged = false;
                AppLogger.Write("✅ Установка завершена — регион восстановлен");
                SetProgress(true, "✅ Офис установлен!", 100, "Регион восстановлен");

                if (!SaveInstaller)
                {
                    TryDeleteDownloadedInstaller();
                }
            }
            catch (OperationCanceledException)
            {
                AppLogger.Write("⏹️ Установка отменена");
                SetProgress(true, "⏹️ Отменено", 0, "");
            }
            catch (Exception ex)
            {
                AppLogger.Write($"❌ Ошибка установки: {ex.Message}");
                SetProgress(true, "❌ Ошибка установки", 0, "");
                MessageBox.Show("Не удалось установить Office. Попробуйте ещё раз или установите вручную.",
                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                InstallationService.InstallSemaphore.Release();

                if (regionChanged)
                {
                    RestoreRegion();
                    AppLogger.Write("🔁 Регион восстановлен (аварийный сброс)");
                }
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
                Application.Current.Dispatcher.Invoke(() =>
                {
                    IsInstalling = false;
                    CancelEnabled = false;
                    HasDownloadedInstaller = _downloadedFilePath != null && File.Exists(_downloadedFilePath);
                });
            }
        }

        private void TryDeleteDownloadedInstaller()
        {
            if (_downloadedFilePath == null)
                return;

            try { File.Delete(_downloadedFilePath); } catch { }
            _downloadedFilePath = null;
        }

        // ── Помощники для процессов C2R ───────────────────────────────────────

        private static HashSet<int> GetC2RProcessPids()
        {
            var names = new[] { "officec2rclient", "OfficeClickToRun" };
            var pids  = new HashSet<int>();
            foreach (var name in names)
                foreach (var p in System.Diagnostics.Process.GetProcessesByName(name))
                    using (p) pids.Add(p.Id);
            return pids;
        }

        private static async Task<System.Diagnostics.Process?> WaitForC2RProcess(
            HashSet<int> existingPids, TimeSpan timeout, CancellationToken token)
        {
            var deadline = DateTime.UtcNow + timeout;
            var names    = new[] { "officec2rclient", "OfficeClickToRun" };

            while (DateTime.UtcNow < deadline && !token.IsCancellationRequested)
            {
                foreach (var name in names)
                    foreach (var p in System.Diagnostics.Process.GetProcessesByName(name))
                    {
                        // Найденный процесс возвращаем (его освобождает вызывающий),
                        // остальные снимки процессов освобождаем сразу.
                        if (!existingPids.Contains(p.Id))
                            return p;
                        p.Dispose();
                    }

                await Task.Delay(2000, token);
            }
            return null;
        }

        private async Task MonitorInstallation(System.Diagnostics.Process proc, CancellationToken token)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(60);
            var elapsed  = System.Diagnostics.Stopwatch.StartNew();

            while (!proc.HasExited && DateTime.UtcNow < deadline && !token.IsCancellationRequested)
            {
                await Task.Delay(5000, token);
                SetDetail($"Установка идёт {elapsed.Elapsed:mm\\:ss}...");
            }

            if (!proc.HasExited)
                AppLogger.Write("⚠️ Таймаут ожидания — продолжаем без подтверждения");
        }
    }
}
```

- [ ] **Step 4: Создать `Ven4Tools/ViewModels/OfficeViewModel.Region.cs`**

Полное содержимое файла:

```csharp
using System;
using System.Windows;
using Microsoft.Win32;
using Ven4Tools.Services;

namespace Ven4Tools.ViewModels
{
    public sealed partial class OfficeViewModel
    {
        // ── Отображение региона (читаем реестр напрямую — изменения видны сразу) ──

        private void UpdateRegionDisplay()
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                // Windows GeoID — читаем прямо из реестра, чтобы изменения были видны сразу
                try
                {
                    using var geo = Registry.CurrentUser.OpenSubKey(@"Control Panel\International\Geo");
                    string? name   = geo?.GetValue("Name")?.ToString();
                    string? nation = geo?.GetValue("Nation")?.ToString();
                    RegionGeoText = (name, nation) switch
                    {
                        ({ } n, { } id) => $"{n} (GeoID: {id})",
                        ({ } n, _)      => n,
                        (_, { } id)     => $"GeoID: {id}",
                        _               => "недоступен"
                    };
                }
                catch { RegionGeoText = "ошибка чтения"; }

                // Office CountryCode
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(
                        @"Software\Microsoft\Office\16.0\Common\ExperimentConfigs\Ecs");
                    string? raw = key?.GetValue("CountryCode")?.ToString();
                    RegionCCText = raw == null
                        ? "не задан"
                        : raw.StartsWith("std::wstring|") ? raw["std::wstring|".Length..] : raw;
                }
                catch { RegionCCText = "недоступен"; }
            });
        }

        // ── Сохранение / смена / восстановление региона ───────────────────────

        private void SaveRegion()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Office\16.0\Common\ExperimentConfigs\Ecs");
                _originalOfficeCC = key?.GetValue("CountryCode")?.ToString();
            }
            catch { _originalOfficeCC = null; }

            try
            {
                using var geo = Registry.CurrentUser.OpenSubKey(@"Control Panel\International\Geo");
                _originalGeoName   = geo?.GetValue("Name")?.ToString();
                _originalGeoNation = geo?.GetValue("Nation")?.ToString();
            }
            catch { _originalGeoName = _originalGeoNation = null; }

            // Persistent-маркер: сохраняем исходный регион на диск ДО SetRegionUS(),
            // чтобы при аварийном завершении его можно было восстановить при следующем
            // запуске. Сама работа с маркером — в OfficeRegionRecoveryService: вкладка
            // создаётся лениво, поэтому восстановление не может жить в её конструкторе.
            OfficeRegionRecoveryService.Save(_originalOfficeCC, _originalGeoName, _originalGeoNation);
        }

        // Восстановление региона из persistent-маркера при открытии вкладки.
        // Основной вызов теперь при старте клиента (App), здесь остаётся как страховка
        // на случай, если маркер появился уже после старта, и ради обновления полей UI.
        private void RecoverRegionFromBackup()
        {
            if (OfficeRegionRecoveryService.Recover())
                UpdateRegionDisplay();
        }

        private void SetRegionUS()
        {
            // Office ExperimentConfigs CountryCode
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Office\16.0\Common\ExperimentConfigs\Ecs");
                key?.SetValue("CountryCode", "std::wstring|US", RegistryValueKind.String);
            }
            catch (Exception ex) { AppLogger.Write($"⚠️ Office CountryCode: {ex.Message}"); }

            // Windows GeoID (Name = код ISO-3166 alpha-2, Nation = числовой GeoID)
            try
            {
                using var geo = Registry.CurrentUser.OpenSubKey(@"Control Panel\International\Geo", writable: true);
                if (geo != null)
                {
                    geo.SetValue("Name",   "US",  RegistryValueKind.String);
                    geo.SetValue("Nation", "244", RegistryValueKind.String);
                }
                else
                    AppLogger.Write("⚠️ Control Panel\\International\\Geo — ключ не найден");
            }
            catch (Exception ex) { AppLogger.Write($"⚠️ Windows GeoID: {ex.Message}"); }

            UpdateRegionDisplay();
        }

        private void RestoreRegion()
        {
            // Office CountryCode
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Office\16.0\Common\ExperimentConfigs\Ecs", writable: true);
                if (key != null)
                {
                    if (_originalOfficeCC != null)
                        key.SetValue("CountryCode", _originalOfficeCC, RegistryValueKind.String);
                    else
                        key.DeleteValue("CountryCode", throwOnMissingValue: false);
                }
            }
            catch (Exception ex) { AppLogger.Write($"⚠️ Восстановление Office CC: {ex.Message}"); }

            // Windows GeoID
            try
            {
                using var geo = Registry.CurrentUser.OpenSubKey(@"Control Panel\International\Geo", writable: true);
                if (geo != null)
                {
                    if (_originalGeoName != null)
                        geo.SetValue("Name", _originalGeoName, RegistryValueKind.String);
                    else
                        geo.DeleteValue("Name", throwOnMissingValue: false);

                    if (_originalGeoNation != null)
                        geo.SetValue("Nation", _originalGeoNation, RegistryValueKind.String);
                    else
                        geo.DeleteValue("Nation", throwOnMissingValue: false);
                }
            }
            catch (Exception ex) { AppLogger.Write($"⚠️ Восстановление Windows GeoID: {ex.Message}"); }

            // Регион восстановлен — удаляем persistent-маркер, он больше не нужен.
            OfficeRegionRecoveryService.Delete();

            UpdateRegionDisplay();
        }
    }
}
```

- [ ] **Step 5: Написать `tests/Ven4Tools.Tests/OfficeViewModelTests.cs`**

Полное содержимое файла:

```csharp
using Ven4Tools.ViewModels;
using Xunit;

namespace Ven4Tools.Tests
{
    public class OfficeViewModelTests
    {
        [Fact]
        public void ResolveVersion_O2024_ВозвращаетOffice2024()
        {
            var (name, id) = OfficeViewModel.ResolveVersion(o2024: true, o2021: false, o2019: false, o2016: false);

            Assert.Equal("Office 2024 ProPlus", name);
            Assert.Equal("ProPlus2024Retail", id);
        }

        [Fact]
        public void ResolveVersion_O2021_ВозвращаетOffice2021()
        {
            var (name, id) = OfficeViewModel.ResolveVersion(false, true, false, false);

            Assert.Equal("Office 2021 Professional", name);
            Assert.Equal("Professional2021Retail", id);
        }

        [Fact]
        public void ResolveVersion_O2019_ВозвращаетOffice2019()
        {
            var (name, id) = OfficeViewModel.ResolveVersion(false, false, true, false);

            Assert.Equal("Office 2019 Professional", name);
            Assert.Equal("Professional2019Retail", id);
        }

        [Fact]
        public void ResolveVersion_O2016_ВозвращаетOffice2016()
        {
            var (name, id) = OfficeViewModel.ResolveVersion(false, false, false, true);

            Assert.Equal("Office 2016 Professional", name);
            Assert.Equal("ProPlusRetail", id);
        }

        [Fact]
        public void ResolveVersion_НичегоНеВыбрано_ВозвращаетOffice365Fallback()
        {
            var (name, id) = OfficeViewModel.ResolveVersion(false, false, false, false);

            Assert.Equal("Office 365 ProPlus", name);
            Assert.Equal("O365ProPlusRetail", id);
        }

        [Fact]
        public void ResolveVersion_ПриоритетO2024НадОстальными()
        {
            var (name, _) = OfficeViewModel.ResolveVersion(true, true, true, true);

            Assert.Equal("Office 2024 ProPlus", name);
        }

        [Fact]
        public void Конструктор_УстанавливаетДефолтныеЗначения()
        {
            var vm = new OfficeViewModel();

            Assert.True(vm.IsO365Selected);
            Assert.False(vm.IsO2024Selected);
            Assert.False(vm.IsO2021Selected);
            Assert.False(vm.IsO2019Selected);
            Assert.False(vm.IsO2016Selected);
            Assert.Equal("ru-ru", vm.SelectedLanguage);
            Assert.False(vm.SaveInstaller);
            Assert.False(vm.HasDownloadedInstaller);
            Assert.False(vm.IsDownloading);
            Assert.False(vm.IsInstalling);
            Assert.False(vm.CancelEnabled);
            Assert.True(vm.CancelVisible);
            Assert.False(vm.ProgressVisible);
            Assert.Equal("⏳ Подготовка...", vm.InstallPhaseText);
            Assert.Equal(0, vm.ProgressValue);
            Assert.Equal("", vm.InstallDetailText);
            Assert.False(vm.ProgressIndeterminate);
            Assert.True(vm.ActivationHintVisible);
        }

        [Fact]
        public void ВыборДругойВерсии_ОбновляетСвойство()
        {
            var vm = new OfficeViewModel();

            vm.IsO2021Selected = true;

            Assert.True(vm.IsO2021Selected);
        }

        [Fact]
        public void DownloadCommand_CanExecute_ИзначальноTrue()
        {
            var vm = new OfficeViewModel();

            Assert.True(vm.DownloadCommand.CanExecute(null));
        }

        [Fact]
        public void InstallCommand_CanExecute_ИзначальноFalse()
        {
            var vm = new OfficeViewModel();

            Assert.False(vm.InstallCommand.CanExecute(null));
        }

        [Fact]
        public void CancelCommand_CanExecute_ИзначальноFalse()
        {
            // Прямо требуется существующим UI-тестом OfficeTab_ОтменаИПереходКАктивации
            // (Ven4Tools.ClientUITests/Phase3RemainingTabsTests.cs) — кнопка «Отмена»
            // обязана быть задизейблена вне активной операции.
            var vm = new OfficeViewModel();

            Assert.False(vm.CancelCommand.CanExecute(null));
        }

        [Fact]
        public void InstallCommand_CanExecute_TrueПослеHasDownloadedInstaller()
        {
            var vm = new OfficeViewModel();

            vm.HasDownloadedInstaller = true;

            Assert.True(vm.InstallCommand.CanExecute(null));
        }

        [Fact]
        public void GoActivationCommand_ПоднимаетСобытие()
        {
            var vm = new OfficeViewModel();
            bool raised = false;
            vm.GoToActivation += () => raised = true;

            vm.GoActivationCommand.Execute(null);

            Assert.True(raised);
        }

        [Fact]
        public void OfficeLanguages_СодержитВосемьЯзыковНачинаясRuRu()
        {
            var vm = new OfficeViewModel();

            Assert.Equal(8, vm.OfficeLanguages.Length);
            Assert.Equal("ru-ru", vm.OfficeLanguages[0]);
        }
    }
}
```

- [ ] **Step 6: Проверить сборку**

Run: `dotnet build Ven4Tools.sln -c Release -warnaserror --no-incremental`
Expected: 0 ошибок, 0 предупреждений.

- [ ] **Step 7: Commit**

```bash
git add Ven4Tools/ViewModels/OfficeViewModel.cs Ven4Tools/ViewModels/OfficeViewModel.Download.cs Ven4Tools/ViewModels/OfficeViewModel.Install.cs Ven4Tools/ViewModels/OfficeViewModel.Region.cs tests/Ven4Tools.Tests/OfficeViewModelTests.cs
git commit -m "feat(office): OfficeViewModel (4 partial-файла) + юнит-тесты"
```

---

### Task 2: Переписать `OfficeTab.xaml`/`OfficeTab.xaml.cs` на тонкую обёртку

**Files:**
- Modify: `Ven4Tools/Views/Tabs/OfficeTab.xaml`
- Modify: `Ven4Tools/Views/Tabs/OfficeTab.xaml.cs`
- Delete: `Ven4Tools/Views/Tabs/OfficeTab.Download.cs`
- Delete: `Ven4Tools/Views/Tabs/OfficeTab.Install.cs`
- Delete: `Ven4Tools/Views/Tabs/OfficeTab.Region.cs`

**Interfaces:**
- Consumes: `Ven4Tools.ViewModels.OfficeViewModel` (Task 1) — все публичные члены.
- Produces: `OfficeTab` с единственным публичным членом сверх конструктора — `event Action? GoToActivation` (внешний контракт, `MainWindow.xaml.cs:250`).

- [ ] **Step 1: Переписать `Ven4Tools/Views/Tabs/OfficeTab.xaml`**

Полное содержимое файла (меняются: `IsChecked`/`Command`/`ItemsSource`/`SelectedItem`/`Text`/`Value`/`Visibility` у интерактивных элементов; стиль `VersionCardStyle`, статический текст и разметка — не трогаются):

```xml
<UserControl x:Class="Ven4Tools.Views.Tabs.OfficeTab"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             Background="{DynamicResource ContentBackground}">
    <UserControl.Resources>
        <BooleanToVisibilityConverter x:Key="BoolToVis"/>
        <Style x:Key="VersionCardStyle" TargetType="RadioButton">
            <Setter Property="GroupName" Value="OfficeVersion"/>
            <Setter Property="Margin" Value="0,0,10,10"/>
            <Setter Property="Width" Value="178"/>
            <Setter Property="Height" Value="54"/>
            <Setter Property="Cursor" Value="Hand"/>
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="RadioButton">
                        <Border x:Name="border"
                                Background="{DynamicResource CardBackground}"
                                BorderBrush="{DynamicResource BorderBrush}"
                                BorderThickness="1.5"
                                CornerRadius="8">
                            <TextBlock x:Name="lbl"
                                       Text="{TemplateBinding Content}"
                                       HorizontalAlignment="Center"
                                       VerticalAlignment="Center"
                                       FontWeight="SemiBold"
                                       FontSize="12"
                                       TextWrapping="Wrap"
                                       TextAlignment="Center"
                                       Padding="8,0"
                                       Foreground="{DynamicResource TextPrimary}"/>
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property="IsMouseOver" Value="True">
                                <Setter TargetName="border" Property="BorderBrush" Value="{DynamicResource AccentColor}"/>
                            </Trigger>
                            <Trigger Property="IsChecked" Value="True">
                                <Setter TargetName="border" Property="Background" Value="{DynamicResource AccentColor}"/>
                                <Setter TargetName="border" Property="BorderBrush" Value="{DynamicResource AccentColor}"/>
                                <Setter TargetName="lbl" Property="Foreground" Value="White"/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>
    </UserControl.Resources>

    <ScrollViewer VerticalScrollBarVisibility="Auto">
        <StackPanel Margin="20">
            <TextBlock Text="Установка Microsoft Office" FontSize="24" FontWeight="Bold"
                       Foreground="{DynamicResource TextPrimary}" Margin="0,0,0,20"/>

            <TextBlock Text="Загрузка официального установщика Office с серверов Microsoft"
                       Foreground="{DynamicResource TextSecondary}" Margin="0,0,0,20" TextWrapping="Wrap"/>

            <!-- Выбор версии — карточки -->
            <GroupBox Header="📦 Выбор версии" Margin="0,0,0,15">
                <WrapPanel Margin="10,10,0,0">
                    <RadioButton x:Name="rdbO365"  Style="{StaticResource VersionCardStyle}" Content="Office 365&#x0a;ProPlus"       IsChecked="{Binding IsO365Selected, Mode=TwoWay}"/>
                    <RadioButton x:Name="rdbO2024" Style="{StaticResource VersionCardStyle}" Content="Office 2024&#x0a;ProPlus"      IsChecked="{Binding IsO2024Selected, Mode=TwoWay}"/>
                    <RadioButton x:Name="rdbO2021" Style="{StaticResource VersionCardStyle}" Content="Office 2021&#x0a;Professional" IsChecked="{Binding IsO2021Selected, Mode=TwoWay}"/>
                    <RadioButton x:Name="rdbO2019" Style="{StaticResource VersionCardStyle}" Content="Office 2019&#x0a;Professional" IsChecked="{Binding IsO2019Selected, Mode=TwoWay}"/>
                    <RadioButton x:Name="rdbO2016" Style="{StaticResource VersionCardStyle}" Content="Office 2016&#x0a;Professional" IsChecked="{Binding IsO2016Selected, Mode=TwoWay}"/>
                </WrapPanel>
            </GroupBox>

            <!-- Язык -->
            <GroupBox Header="🌐 Язык интерфейса" Margin="0,0,0,15">
                <Grid Margin="10">
                    <ComboBox x:Name="cmbOfficeLanguage" Height="35" Background="White" Foreground="Black"
                              ItemsSource="{Binding OfficeLanguages}"
                              SelectedItem="{Binding SelectedLanguage, Mode=TwoWay}"/>
                </Grid>
            </GroupBox>

            <CheckBox x:Name="chkSaveInstaller" Content="Сохранить установщик после установки"
                      Foreground="{DynamicResource TextPrimary}" Margin="0,0,0,10"
                      IsChecked="{Binding SaveInstaller, Mode=TwoWay}"/>

            <WrapPanel Margin="0,10,0,10">
                <Button x:Name="btnDownloadOffice" Content="📥 Скачать"
                        ToolTip="Скачает установочные файлы выбранной версии и языка Office с серверов Microsoft."
                        Height="45" Width="205"
                        Background="{StaticResource BrandGreen}" Foreground="#06130D" FontWeight="Bold" FontSize="14"
                        Margin="0,0,10,10"
                        Command="{Binding DownloadCommand}"/>
                <Button x:Name="btnInstallOffice" Content="🚀 Установить"
                        ToolTip="Запустит установку Office с выбранными параметрами из ранее скачанных файлов."
                        Height="45" Width="205"
                        Background="{StaticResource BrandGreen}" Foreground="#06130D" FontWeight="Bold" FontSize="14"
                        Margin="0,0,10,10"
                        Command="{Binding InstallCommand}"/>
                <Button x:Name="btnCancelOffice" Content="⏹️ Отмена"
                        ToolTip="Остановит текущую загрузку или подготовку Office. Уже скачанные файлы могут сохраниться."
                        Height="45" Width="120"
                        FontWeight="Bold" FontSize="13"
                        Margin="0,0,0,10"
                        Command="{Binding CancelCommand}"
                        Visibility="{Binding CancelVisible, Converter={StaticResource BoolToVis}}"/>
            </WrapPanel>

            <!-- Статус региона -->
            <Border Background="{DynamicResource CardBackground}"
                    BorderBrush="{DynamicResource BorderBrush}" BorderThickness="1"
                    CornerRadius="6" Padding="12,8" Margin="0,0,0,12">
                <StackPanel Orientation="Horizontal">
                    <TextBlock Text="🌐 " VerticalAlignment="Center" FontSize="13"/>
                    <TextBlock Text="Регион Windows: " Foreground="{DynamicResource TextSecondary}"
                               VerticalAlignment="Center" FontSize="12"/>
                    <TextBlock x:Name="txtRegionGeo" Text="{Binding RegionGeoText}" FontWeight="SemiBold" FontSize="12"
                               Foreground="{DynamicResource TextPrimary}" VerticalAlignment="Center"/>
                    <TextBlock Text="   ·   Office CountryCode: " Foreground="{DynamicResource TextSecondary}"
                               VerticalAlignment="Center" FontSize="12" Margin="8,0,0,0"/>
                    <TextBlock x:Name="txtRegionCC" Text="{Binding RegionCCText}" FontWeight="SemiBold" FontSize="12"
                               Foreground="{DynamicResource TextPrimary}" VerticalAlignment="Center"/>
                </StackPanel>
            </Border>

            <!-- Прогресс -->
            <Border x:Name="pnlProgress"
                    Visibility="{Binding ProgressVisible, Converter={StaticResource BoolToVis}}"
                    Background="{DynamicResource CardBackground}"
                    BorderBrush="{DynamicResource BorderBrush}" BorderThickness="1"
                    CornerRadius="8" Padding="16,12" Margin="0,0,0,16">
                <StackPanel>
                    <TextBlock x:Name="txtInstallPhase"
                               Text="{Binding InstallPhaseText}"
                               FontWeight="Bold" FontSize="13"
                               Foreground="{DynamicResource TextPrimary}"
                               Margin="0,0,0,8"/>
                    <ProgressBar x:Name="progressOffice"
                                 Height="8" Minimum="0" Maximum="100" Value="{Binding ProgressValue}"
                                 IsIndeterminate="{Binding ProgressIndeterminate}"
                                 Foreground="{DynamicResource AccentColor}"
                                 Background="{DynamicResource BorderBrush}"/>
                    <TextBlock x:Name="txtInstallDetail"
                               Text="{Binding InstallDetailText}"
                               FontSize="11" Margin="0,6,0,0"
                               Foreground="{DynamicResource TextSecondary}"/>
                </StackPanel>
            </Border>

            <TextBlock Text="ℹ️ Примечание: регион Windows временно переключается на US для обхода блокировок и восстанавливается только после завершения установки."
                       Foreground="{DynamicResource TextSecondary}" FontSize="11" TextWrapping="Wrap"/>
            <TextBlock Text="⚠️ Требуется подключение к интернету для скачивания установщика."
                       Foreground="#FFA500" FontSize="11" Margin="0,5,0,0"/>
            <TextBlock Text="🔑 Для использования Microsoft Office требуется действующая лицензия Microsoft. Ven4Tools только скачивает официальный установщик."
                       Foreground="{DynamicResource TextSecondary}" FontSize="11" TextWrapping="Wrap" Margin="0,5,0,0"/>

            <!-- Кнопка активации — только для авторизованных -->
            <Border x:Name="pnlActivationHint"
                    Visibility="{Binding ActivationHintVisible, Converter={StaticResource BoolToVis}}"
                    Background="{DynamicResource CardBackground}"
                    BorderBrush="{DynamicResource AccentColor}"
                    BorderThickness="1" CornerRadius="8"
                    Padding="16,12" Margin="0,20,0,0">
                <DockPanel>
                    <Button x:Name="btnGoActivation" DockPanel.Dock="Right"
                            Content="🔑 Активация →"
                            ToolTip="Откроет вкладку управления лицензиями Office. Активация автоматически не выполняется."
                            Height="36" Padding="16,0"
                            FontWeight="Bold" FontSize="13"
                            Command="{Binding GoActivationCommand}"/>
                    <TextBlock Text="Office уже установлен? Перейдите к активации."
                               Foreground="{DynamicResource TextSecondary}"
                               VerticalAlignment="Center" TextWrapping="Wrap"
                               Margin="0,0,12,0"/>
                </DockPanel>
            </Border>

        </StackPanel>
    </ScrollViewer>
</UserControl>
```

- [ ] **Step 2: Переписать `Ven4Tools/Views/Tabs/OfficeTab.xaml.cs`**

Полное содержимое файла:

```csharp
using System;
using System.Windows.Controls;
using Ven4Tools.ViewModels;

namespace Ven4Tools.Views.Tabs
{
    /// <summary>
    /// Вкладка «Office» — тонкая обёртка над <see cref="OfficeViewModel"/>.
    /// Вся логика перенесена в ViewModel при MVVM-миграции (2026-08-25, шестая
    /// вкладка после DebloaterTab/HistoryTab/AboutTab/ActivationTab/NetworkTab).
    /// Единственный публичный член сверх конструктора — <see cref="GoToActivation"/>,
    /// внешний контракт: MainWindow.xaml.cs подписывается на него напрямую.
    /// </summary>
    public partial class OfficeTab : UserControl
    {
        private readonly OfficeViewModel _viewModel = new();

        public event Action? GoToActivation;

        public OfficeTab()
        {
            InitializeComponent();
            DataContext = _viewModel;
            _viewModel.GoToActivation += () => GoToActivation?.Invoke();
        }
    }
}
```

- [ ] **Step 3: Удалить перенесённые partial-файлы code-behind**

```bash
git rm Ven4Tools/Views/Tabs/OfficeTab.Download.cs Ven4Tools/Views/Tabs/OfficeTab.Install.cs Ven4Tools/Views/Tabs/OfficeTab.Region.cs
```

- [ ] **Step 4: Проверить сборку**

Run: `dotnet build Ven4Tools.sln -c Release -warnaserror --no-incremental`
Expected: 0 ошибок, 0 предупреждений — во всех проектах, включая `Ven4Tools.ClientUITests`.

- [ ] **Step 5: Commit**

```bash
git add Ven4Tools/Views/Tabs/OfficeTab.xaml Ven4Tools/Views/Tabs/OfficeTab.xaml.cs
git commit -m "refactor(office): OfficeTab — тонкая обёртка над OfficeViewModel"
```

---

### Task 3: Верификация — регрессия существующих тестов

**Files:**
- Не создаёт и не меняет файлы.

**Interfaces:**
- Не применимо.

- [ ] **Step 1: Полная сборка Release**

Run: `dotnet build Ven4Tools.sln -c Release -warnaserror --no-incremental`
Expected: 0/0.

- [ ] **Step 2: Юнит-тесты целиком на VenchWork**

Run (на VenchWork): `dotnet test tests/Ven4Tools.Tests -c Release`
Expected: было 433/433 после NetworkTab (см. память `project_ven4tools_mvvm_migration_networktab_2026_08_25`) + 14 новых из `OfficeViewModelTests` = 447/447.

- [ ] **Step 3: Существующие UI-тесты на VenchWork**

Run (на VenchWork): `dotnet test Ven4Tools.ClientUITests -c Release --filter "FullyQualifiedName~Phase3RemainingTabsTests|FullyQualifiedName~KeyButtonsSmokeTests"`
Expected: `OfficeTab_ОтменаИПереходКАктивации` и все остальные тесты обоих классов — зелёные, не хуже прежнего результата (13/13 после NetworkTab). **Реальное скачивание/установку Office НЕ запускать** — тест кликает только `btnGoActivation`/проверяет `btnCancelOffice.IsEnabled`, не `btnDownloadOffice`.

- [ ] **Step 4: Финальный коммит верификации**

```bash
git add -A
git status
git commit -m "test(office): MVVM-миграция OfficeTab проверена на VenchWork" --allow-empty
```

- [ ] **Step 5: Финальное цельное ревью ветки**

Обязательный шаг перед мерджем (см. Global Constraints и критерий готовности спеки) — точечные ревью Task 1/Task 2 структурно не видят межзадачные пробелы; в предыдущих 4 вкладках подряд этот шаг находил реальные находки. Пакет для ревью: `scripts/review-package <merge-base main mvvm-officetab> HEAD`.

- [ ] **Step 6: Merge + push в `main`** (без дополнительного вопроса — автономная сессия)

```bash
git checkout main
git merge --ff-only mvvm-officetab
dotnet build Ven4Tools.sln -c Release -warnaserror --no-incremental
git push origin main
git branch -d mvvm-officetab
```

Перед пушем — обязательно проверить все коммиты ветки на `Claude-Session`-трейлер: `git log main..mvvm-officetab --format="%B" | grep -i claude` (должно быть пусто).

---

## После задачи

Смержено и запушено в `main`. Следующая по сложности вкладка — `InstalledTab` (771 строка) — тот же процесс, новая ветка от `main`.
