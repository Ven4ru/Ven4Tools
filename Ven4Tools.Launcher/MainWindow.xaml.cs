using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Windows;
using System.Windows.Forms;
using Ven4Tools.Launcher.Models;
using Ven4Tools.Launcher.Services;
using Ven4Tools.Shared;

namespace Ven4Tools.Launcher
{
    public partial class MainWindow : Window
    {
        // Один переиспользуемый клиент: создание нового HttpClient на каждый запрос
        // исчерпывает сокеты. Таймаут — бесконечный, отдельные операции ограничиваем
        // через CancellationToken на месте вызова.
        private static readonly HttpClient _httpClient = CreateHttpClient();

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.Add("User-Agent", "Ven4Tools-Launcher");
            return client;
        }

        private NotifyIcon?          _notifyIcon;
        private bool                 _minimizeToTray = true;
        private string               _settingsPath;
        private string               _installPath = "";
        private string               _clientPath  = "";
        private List<ClientVersionInfo> _availableVersions = new();
        private ClientVersionInfo?   _selectedVersion;
        private bool                 _clientUpdateAvailable = false;
        private bool                 _detailsPanelOpen = false;
        private System.Diagnostics.Process? _clientProcess;
        // Слот долгих операций: скачивание/обновление клиента, тихое автообновление,
        // установка из локального архива, установка компонентов. Все они делят одну
        // полосу прогресса, одну строку статуса, одну кнопку «Отмена» и один каталог
        // установки, поэтому выполняются строго по одной — см. OperationGate.
        private readonly OperationGate _operations = new OperationGate();
        private UpdateBackgroundService? _updateService;
        private bool                 _backgroundUpdates = true;
        private bool                 _autostart         = false;
        private bool                 _startMinimized    = false;
        private bool                 _autoUpdateClient  = false;
        // Выбранный пользователем источник загрузки (переставляется в начало цепочки).
        private DownloadSource       _downloadSource    = DownloadSource.Auto;
        // Кэш последнего известного IP CDN из подписанного version.json (для IP-pinning).
        private string               _lastKnownCdnIp    = "";
        private SettingsWindow?      _settingsWindow;
        private readonly string      _dataFolderPath;
        // Запросы на установку компонентов из setup отложены до первого видимого показа
        // окна: при автозапуске в трее (скрытое окно) UAC/прогресс не должны всплывать незаметно
        private bool                 _pendingSetupComponents = false;
        // Окна отчётов (крэш клиента / неуспешные установки) при старте показываются
        // модально; при автозапуске в трее (скрытое окно) — откладываются до первого
        // показа окна, чтобы модальный диалог не всплывал поверх скрытого владельца.
        private bool                 _pendingStartupReports = false;
        private readonly bool        _isUiTestMode;
        private string               _lastNotifiedLauncherVersion = "";
        private string               _lastNotifiedClientVersion   = "";
        private string               _lastNotifiedNotificationId  = "";
        private ToolStripMenuItem?   _trayItemAutostart;
        private ToolStripMenuItem?   _trayItemBgUpdates;
        private WatchdogService?     _watchdog;

        public MainWindow()
        {
            InitializeComponent();

            // Метка версии в сайдбаре — раньше был захардкожен литерал "LAUNCHER  2.1",
            // расходившийся с реальной версией сборки (Gap Analysis).
            var assemblyVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            txtLauncherVersionLabel.Text = assemblyVersion != null
                ? $"LAUNCHER  {assemblyVersion.Major}.{assemblyVersion.Minor}"
                : "LAUNCHER";

            logExpander.Expanded += LogExpander_Expanded;
            MotionService.Enabled = Environment.GetEnvironmentVariable("VEN4TOOLS_REDUCE_MOTION") != "1";
            Loaded += (_, _) =>
            {
                MotionService.FadeIn(this);
                MotionService.Pulse(btnLaunchApp, 1.025, 220);
            };
            _isUiTestMode = Environment.GetEnvironmentVariable("VEN4TOOLS_UI_TEST") == "1";

            string appData = _isUiTestMode
                ? Environment.GetEnvironmentVariable("VEN4TOOLS_UI_TEST_ROOT")
                    ?? Path.Combine(Path.GetTempPath(), "Ven4Tools.UI.Tests")
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Ven4Tools");
            Directory.CreateDirectory(appData);
            _settingsPath   = Path.Combine(appData, "launcher_settings.json");
            _dataFolderPath = appData;

            LoadSettings();
            if (_isUiTestMode)
                _minimizeToTray = false;
            if (!_isUiTestMode)
                CreateTrayIcon();

            if (string.IsNullOrEmpty(_installPath))
                _installPath = _isUiTestMode
                    ? Path.Combine(appData, "Install")
                    : ResolveInitialInstallRoot();

            _clientPath = ResolveClientPath();
            // Cleanup — ДО CreateDirectory: иначе target уже существует (пустым) к моменту
            // проверки, и восстановление .backup-* при прерванной установке никогда не сработает.
            if (!_isUiTestMode)
            {
                var cleanup = CleanupStaleInstallArtifacts(_clientPath);
                // Молчим, когда чистить было нечего (обычный запуск), — иначе строка
                // висела бы в журнале каждый раз и обесценивала бы сама себя. Зато
                // после прерванной установки пользователь наконец видит, что именно
                // лаунчер сделал с остатками, вместо необъяснимой тишины.
                if (cleanup.AnythingHappened)
                {
                    AddLog($"🧹 Остатки прерванной установки: восстановлено {cleanup.Restored}, удалено {cleanup.Removed}");
                }
            }
            // Папку клиента здесь НЕ создаём. Раньше конструктор делал это
            // безусловно, и у каждого, кто лаунчер только запустил, появлялся пустой
            // каталог Ven4Tools_Client — с переездом умолчания в «Документы» этот
            // мусор стал попадать пользователю на глаза. Каталог создаётся тем, кто в
            // него пишет: TransactionalDirectoryInstaller при установке сам переносит
            // туда staging и корректно работает с несуществующим target.
            txtInstallPath.Text = _isUiTestMode ? @"C:\Ven4Tools-Test\Client" : _clientPath;

            // Начальное состояние главной кнопки выставляем синхронно по наличию клиента
            // на диске — ещё до сетевых проверок, чтобы не было заметного мигания
            // зелёной «Установить Ven4Tools» → «Загрузить/Запустить» (L3). quiet — без
            // записи в журнал, строку добавит последующий LoadVersionsAsync.
            if (!_isUiTestMode)
                CheckExistingClient(quiet: true);

            // Фоновый сервис запускается после установки _clientPath:
            // он читает путь клиента при первой проверке обновлений
            if (!_isUiTestMode)
                StartBackgroundService();

            Loaded += async (s, e) =>
            {
                if (_isUiTestMode)
                    return;

                // Лаунчер запущен не из папки установки — предлагаем установить.
                // Если пользователь согласился и установщик запущен — выходим.
                var installSvc = new LauncherUpdateService(AddLog, _downloadSource);
                if (await installSvc.OfferInstallationAsync())
                {
                    ExitApplication();
                    return;
                }

                if (_startMinimized)
                {
                    // Окно скрыто (автозапуск в трее) — не запускаем установку выбранных
                    // в setup компонентов незаметно. Откладываем до первого показа окна
                    // из трея, где UAC-диалоги и прогресс будут видны пользователю.
                    Hide();
                    _pendingSetupComponents = true;
                }
                else
                {
                    await ProcessSetupComponentRequestsAsync();
                }
                await LoadVersionsAsync();
                await CheckComponentsAutoAsync();

                // Модальные отчёты не показываем поверх скрытого окна (автозапуск в
                // трее) — откладываем до первого показа из трея (см. ShowWindow).
                if (_startMinimized)
                    _pendingStartupReports = true;
                else
                    ShowStartupReports();
            };
        }

        /// <summary>
        /// Папка установленного клиента. Обычно это <c>&lt;папка установки&gt;\Ven4Tools_Client</c>,
        /// но «Найти клиент на диске» умеет привязать лаунчер к папке с любым именем
        /// (скажем, <c>D:\Games\MyVen4Tools</c>). Раньше в настройки писалась только папка
        /// установки, а этот путь пересобирался при каждом старте по шаблону — то есть
        /// привязка жила ровно до перезапуска лаунчера, после чего клиент «пропадал» и
        /// его предлагалось скачать заново.
        ///
        /// Сохранённый путь всё равно перепроверяется: файл настроек можно отредактировать
        /// руками, а по этому пути идут рекурсивное удаление («Удалить клиент») и
        /// транзакционная установка, которая целиком заменяет каталог.
        /// </summary>
        private string ResolveClientPath()
        {
            string derived = Path.Combine(_installPath, "Ven4Tools_Client");
            if (_isUiTestMode || string.IsNullOrWhiteSpace(_clientPath)) return derived;

            if (!InstallPathGuard.IsClientPathSafe(_clientPath, _dataFolderPath))
            {
                AddLog($"⚠️ Сохранённая папка клиента небезопасна ({_clientPath}) — возвращаюсь к {derived}");
                return derived;
            }

            return _clientPath;
        }

        /// <summary>
        /// Куда ставить клиент, когда в настройках пути ещё нет (первый запуск).
        /// По умолчанию — «Документы» пользователя: раньше здесь стояла папка самого
        /// лаунчера (<c>%LocalAppData%\Ven4Tools\Launcher</c>), и клиент разворачивался
        /// внутри служебного каталога профиля, куда пользователь не заглядывает и где
        /// его не ожидает найти.
        ///
        /// Исключение — уже установленный клиент рядом с exe лаунчера: так работали
        /// все версии до этой, и смена умолчания не должна «терять» рабочую копию у
        /// того, у кого настройки не сохранились (битый или удалённый
        /// launcher_settings.json). Такой пользователь остаётся на своём месте, а
        /// перенести папку может кнопкой «Изменить…» в карточке пути.
        /// </summary>
        private static string ResolveInitialInstallRoot()
        {
            string legacyRoot = AppDomain.CurrentDomain.BaseDirectory;
            try
            {
                if (File.Exists(Path.Combine(legacyRoot, "Ven4Tools_Client", "Ven4Tools.exe")))
                    return legacyRoot;
            }
            catch { /* путь недоступен — просто идём за «Документами» */ }

            try
            {
                // SpecialFolderOption.Create: папка «Документы» может быть перенаправлена
                // (OneDrive, сетевой профиль) и физически ещё не существовать — тогда её
                // создаёт сама ОС по актуальному пути перенаправления.
                string documents = Environment.GetFolderPath(
                    Environment.SpecialFolder.MyDocuments, Environment.SpecialFolderOption.Create);
                if (!string.IsNullOrWhiteSpace(documents) && Directory.Exists(documents))
                    return documents;
            }
            catch { /* перенаправление на недоступный диск и т.п. — откат ниже */ }

            // Крайний случай: «Документы» недоступны. Профиль пользователя есть всегда,
            // и он точно доступен на запись — лучше, чем вернуться в каталог лаунчера.
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return string.IsNullOrWhiteSpace(profile) ? legacyRoot : profile;
        }

        // Показ отложенных модальных отчётов при старте: крэш клиента и неуспешные
        // установки. Вызывается сразу (окно видимо) либо из ShowWindow при первом
        // раскрытии окна из трея (окно было скрыто при автозапуске).
        private void ShowStartupReports()
        {
            // Параноидальный режим клиента обещает блокировать отправку отчётов.
            // Оба окна ниже предлагают опубликовать данные ПУБЛИЧНЫМ issue на GitHub —
            // ровно то, чего пользователь этим режимом и не хочет. Отложенный краш-отчёт
            // при этом удаляем: клиент делает то же самое при своём старте, но лаунчер
            // обычно доходит до файла раньше, и без этого файл дожидался бы момента,
            // когда режим выключат.
            if (ClientPrivacySettings.IsParanoidMode())
            {
                try { System.IO.File.Delete(LauncherPaths.CrashReportPath); } catch { }
                // То же самое для журнала неудачных установок: его записи предлагаются
                // к публикации тем же ПУБЛИЧНЫМ issue и содержат название с
                // идентификатором приложения, которые у добавленного вручную
                // приложения берутся из имени файла установщика и вполне могут
                // содержать имя пользователя. Раньше журнал переживал включение
                // режима и всплывал при первом запуске с выключенным. Файл не
                // удаляем — его читает клиент для списка «Повторить».
                MarkAllInstallFailuresReported();
                AddLog("🔒 Отчёты клиента не предлагаются: включён параноидальный режим");
                return;
            }

            // Раньше этот метод не оставлял о себе ни строчки: показал окна или нет,
            // почему нет — узнать было нечем. Отчёты о сбоях появляются у пользователя
            // редко и не по команде, поэтому «ничего не произошло» тут неотличимо от
            // «файл не разобрался» без единого следа.
            var crash = ReadCrashReport();
            if (crash != null && !crash.Reported)
            {
                AddLog($"🐛 Клиент завершился с ошибкой ({crash.ExceptionType}) — показываю отчёт");
                var win = new CrashReportWindow(crash) { Owner = this };
                win.ShowDialog();
            }
            else if (crash != null)
            {
                AddLog("🐛 Отчёт о сбое клиента уже был показан ранее — пропускаю");
            }

            var failures = ReadInstallFailures();
            if (failures.Count > 0)
            {
                AddLog($"📋 Неудачных установок в журнале клиента: {failures.Count} — показываю отчёт");
                var win = new InstallReportWindow(failures) { Owner = this };
                win.ShowDialog();
            }
        }

        // Единая точка входа во все долгие операции: либо занимаем слот, либо объясняем,
        // какая именно операция сейчас выполняется. Раньше каждая точка входа решала это
        // сама (а «Установить компоненты» и «Загрузить/Обновить клиент» — не решали
        // вовсе), из-за чего вторая операция перехватывала общий источник отмены у
        // первой. Возвращённую аренду обязан освободить вызывающий (using).
        // silent — путь без пользователя (тихое автообновление, установка компонентов из
        // setup): модальное окно там показывать нельзя, ограничиваемся журналом.
        private OperationLease? TryBeginOperation(string name, TimeSpan timeout, bool silent = false)
        {
            var lease = _operations.TryBegin(name, timeout);
            if (lease != null)
                return lease;

            string busy = _operations.CurrentOperation ?? "другая операция";
            AddLog($"⏳ «{name}»: сейчас выполняется «{busy}» — операция отложена");
            if (!silent)
                System.Windows.MessageBox.Show(
                    $"Сейчас выполняется другая операция: {busy}.\n\n" +
                    "Дождитесь её завершения и повторите.",
                    "Лаунчер занят", MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }

        private void LogExpander_Expanded(object sender, RoutedEventArgs e) =>
            MotionService.FadeIn(txtLog, 180);

        private sealed class LauncherSettings
        {
            public bool    MinimizeToTray              { get; set; } = true;
            public string? InstallPath                 { get; set; }
            // Папка клиента отдельно от папки установки: «Найти клиент на диске»
            // может привязать лаунчер к каталогу с произвольным именем (см. ResolveClientPath).
            public string? ClientPath                  { get; set; }
            public bool    BackgroundUpdates           { get; set; } = true;
            public bool    Autostart                   { get; set; }
            public bool    StartMinimized              { get; set; }
            public bool    AutoUpdateClient             { get; set; }
            public DownloadSource DownloadSource        { get; set; } = DownloadSource.Auto;
            public string? LastKnownCdnIp               { get; set; }
            public string? LastNotifiedLauncherVersion { get; set; }
            public string? LastNotifiedClientVersion   { get; set; }
            public string? LastNotifiedNotificationId  { get; set; }
        }
    }
}
