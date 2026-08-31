using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Ven4Tools.Services;
using Ven4Tools.Shared;
using Ven4Tools.Views;
using Ven4Tools.Views.Tabs;

namespace Ven4Tools
{
    /// <summary>
    /// Главное окно — координатор: навигация по вкладкам, индикаторы в шапке и
    /// подключение выделенных контроллеров. Собственной прикладной логики не держит:
    /// трей — <see cref="TrayIconController"/>, закреплённые приложения —
    /// <see cref="PinsStripController"/> и <see cref="PinnedAppsService"/>,
    /// перетаскивание установщиков — <see cref="InstallerDropHandler"/>,
    /// маскот — <see cref="MascotController"/>, журнал — <see cref="GlobalLogController"/>.
    /// </summary>
    public partial class MainWindow : Window
    {
        private bool _categorySelectionShown = false;
        private string _currentTab = "catalog";
        private bool _feedbackShown = false;

        private CatalogTab?    _catalogTab;
        private InstalledTab?  _installedTab;
        private SystemTab?     _systemTab;
        private DiagnosticsTab? _diagnosticsTab;
        private BenchmarkTab?  _benchmarkTab;
        private WindowsUpdateTab? _windowsUpdateTab;
        private OfficeTab?     _officeTab;
        private ActivationTab? _activationTab;
        private AboutTab?      _aboutTab;
        private NetworkTab?    _networkTab;
        private HistoryTab?    _historyTab;
        private DebloaterTab?  _debloaterTab;

        private readonly GlobalLogController _globalLog;
        private readonly MascotController _mascot;
        private readonly PinsStripController _pins;
        private readonly InstallerDropHandler _drop;
        private readonly TrayIconController _tray;

        private DispatcherTimer? _activeTasksTimer;
        private bool? _lastActiveTasksBusy;

        public MainWindow()
        {
            InitializeComponent();
            txtSidebarVersion.Text = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "—";
            MotionService.Enabled = Environment.GetEnvironmentVariable("VEN4TOOLS_REDUCE_MOTION") != "1";
            Loaded += (_, _) => MotionService.FadeIn(this);

            // Контроллеры создаются до проверки прав: окно уже показано, а XAML-события
            // (drag&drop, кнопки журнала) могут прийти и на пути аварийного перезапуска.
            _globalLog = new GlobalLogController(lstGlobalLog);
            _mascot    = new MascotController(imgMascot);
            _pins      = new PinsStripController(pnlPins, wrapPins, this,
                             () => _catalogTab?.SelectedInstallDrive ?? "C:\\");
            // Именованный обработчик — отписываемся в OnClosed. Источник изменения —
            // либо сама полоса (снятие пина), либо кнопка 📌 в открытой карточке
            // приложения (AppCardViewModel.TogglePinCommand) — у карточки нет прямой
            // ссылки на _pins, только на общий сервис.
            PinnedAppsService.Changed += OnPinsChanged;
            _drop      = new InstallerDropHandler(this, pnlDropOverlay,
                             app => _catalogTab?.AddLocalInstallerApp(app));
            _tray      = new TrayIconController(Dispatcher, ShowFromTray, ForceExit);

            AppLogger.MessageReceived += _globalLog.Append;

            if (!IsRunAsAdmin())
            {
                RestartAsAdmin();
                return;
            }

            NavigateToCatalog(null, null);

            Loaded += (s, e) => ShowCategorySelectionIfNeeded();
            Loaded += (s, e) =>
            {
                _tray.Initialize();
                _pins.Refresh();
            };
            Loaded += (s, e) =>
            {
                ConnectivityMonitor.Start();
                // Именованный обработчик — отписываемся в OnClosed, чтобы не было утечки
                ConnectivityMonitor.StatusChanged += OnConnectivityChanged;
                UpdateTabVisibility();
            };
            Loaded += (_, _) =>
            {
                // Предзагрузка winget list в фоне — пока пользователь смотрит на каталог,
                // InstalledTab уже готов и откроется мгновенно
                InstalledTab.StartPreload();
            };
            Loaded += (s, e) =>
            {
                // Именованный обработчик — отписываемся в OnClosed, чтобы не было утечки
                WindowsUpdateBackgroundService.CountChanged += OnWindowsUpdateCountChanged;
                OnWindowsUpdateCountChanged();
            };
            Loaded += (_, _) =>
            {
                // Пилюля «Нет активных задач» отражает общий семафор установки
                // (InstallationService.IsBusy) — тот же, что используют каталог,
                // история, «Установленные» и Windows Update. Поллинг, а не событие:
                // WaitAsync/Release разбросаны по многим местам, событие пришлось бы
                // добавлять в каждое — таймер безопаснее и ничего не трогает.
                _activeTasksTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                _activeTasksTimer.Tick += (_, _) => UpdateActiveTasksIndicator();
                _activeTasksTimer.Start();
                UpdateActiveTasksIndicator();
            };
        }

        private void UpdateActiveTasksIndicator()
        {
            bool busy = InstallationService.IsBusy;
            if (_lastActiveTasksBusy == busy) return;
            _lastActiveTasksBusy = busy;

            txtActiveTasks.Text = busy ? "Выполняется установка" : "Нет активных задач";
            var brush = busy ? (Brush)FindResource("BrandGreen") : (Brush)FindResource("TextSecondary");
            txtActiveTasks.Foreground = brush;
            dotActiveTasks.Fill = brush;
        }

        private void OnConnectivityChanged(bool online) =>
            Dispatcher.Invoke(() => UpdateTabVisibility());

        private void OnPinsChanged() => Dispatcher.Invoke(() => _pins.Refresh());

        private void OnWindowsUpdateCountChanged() => Dispatcher.Invoke(() =>
        {
            int count = WindowsUpdateBackgroundService.AvailableCount;
            txtWindowsUpdateBadge.Text = count > 99 ? "99+" : count.ToString();
            badgeWindowsUpdateCount.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        });

        protected override void OnClosed(EventArgs e)
        {
            // Снимаем подписки на статические события, иначе окно не освобождается GC
            AppLogger.MessageReceived -= _globalLog.Append;
            ConnectivityMonitor.StatusChanged -= OnConnectivityChanged;
            WindowsUpdateBackgroundService.CountChanged -= OnWindowsUpdateCountChanged;
            PinnedAppsService.Changed -= OnPinsChanged;
            _tray.UnregisterNotifier();
            _activeTasksTimer?.Stop();
            base.OnClosed(e);
        }

        // ── Навигация ─────────────────────────────────────────────────────────────

        private void NavigateToCatalog(object? sender, RoutedEventArgs? e)
        {
            SetActiveButton(btnCatalogTab);
            if (sender != null) AppLogger.Write("📂 Открыта вкладка: Каталог");
            if (_catalogTab == null)
            {
                _catalogTab = new CatalogTab();
                _catalogTab.SwitchToUpdatesRequested += () =>
                {
                    if (_installedTab == null) _installedTab = new InstalledTab();
                    _installedTab.ShowUpdatesFilter();
                    NavigateToInstalled(null, null);
                };
            }
            MainFrame.Content = (_catalogTab);
            UpdateMascot("catalog");
        }

        private void NavigateToNetwork(object? sender, RoutedEventArgs? e)
        {
            SetActiveButton(btnNetworkTab);
            AppLogger.Write("📂 Открыта вкладка: Сеть");
            if (_networkTab == null) _networkTab = new NetworkTab();
            MainFrame.Content = (_networkTab);
            UpdateMascot("network");
        }

        private void NavigateToInstalled(object? sender, RoutedEventArgs? e)
        {
            SetActiveButton(btnInstalledTab);
            if (sender != null) AppLogger.Write("📂 Открыта вкладка: Установленные");
            if (_installedTab == null) _installedTab = new InstalledTab();
            MainFrame.Content = (_installedTab);
            UpdateMascot("installed");
        }

        private void NavigateToSystem(object? sender, RoutedEventArgs? e)
        {
            SetActiveButton(btnSystemTab);
            AppLogger.Write("📂 Открыта вкладка: Система");
            if (_systemTab == null) _systemTab = new SystemTab();
            MainFrame.Content = (_systemTab);
            UpdateMascot("system");
        }

        private void NavigateToDiagnostics(object? sender, RoutedEventArgs? e)
        {
            SetActiveButton(btnDiagnosticsTab);
            if (sender != null) AppLogger.Write("📂 Открыта вкладка: Диагностика");
            if (_diagnosticsTab == null)
            {
                _diagnosticsTab = new DiagnosticsTab();
                // Найденные ошибки Windows Update — повод сразу открыть вкладку
                // обновлений; тот же приём, что у OfficeTab.GoToActivation.
                _diagnosticsTab.GoToWindowsUpdate += () => NavigateToWindowsUpdate(null, null);
            }
            MainFrame.Content = (_diagnosticsTab);
            UpdateMascot("system"); // отдельного маскота для этой вкладки нет — используем нейтрального "system"
        }

        private void NavigateToBenchmark(object? sender, RoutedEventArgs? e)
        {
            SetActiveButton(btnBenchmarkTab);
            AppLogger.Write("📂 Открыта вкладка: Бенчмарк");
            if (_benchmarkTab == null) _benchmarkTab = new BenchmarkTab();
            MainFrame.Content = (_benchmarkTab);
            UpdateMascot("system"); // отдельного маскота для этой вкладки нет — используем нейтрального "system"
        }

        private void NavigateToWindowsUpdate(object? sender, RoutedEventArgs? e)
        {
            SetActiveButton(btnWindowsUpdateTab);
            if (sender != null) AppLogger.Write("📂 Открыта вкладка: Windows Update");
            if (_windowsUpdateTab == null)
            {
                _windowsUpdateTab = new WindowsUpdateTab();
                // Обратный переход: проверка обновлений ничего не дала — причины
                // ищутся в журнале ошибок на вкладке «Диагностика».
                _windowsUpdateTab.GoToDiagnostics += () => NavigateToDiagnostics(null, null);
            }
            MainFrame.Content = (_windowsUpdateTab);
            UpdateMascot("system"); // отдельного маскота для этой вкладки пока нет — используем нейтрального "system"
        }

        private void NavigateToOffice(object? sender, RoutedEventArgs? e)
        {
            SetActiveButton(btnOfficeTab);
            AppLogger.Write("📂 Открыта вкладка: Office");
            if (_officeTab == null)
            {
                _officeTab = new OfficeTab();
                _officeTab.GoToActivation += () => NavigateToActivation(null, null);
            }
            MainFrame.Content = (_officeTab);
            UpdateMascot("office");
        }

        private void NavigateToActivation(object? sender, RoutedEventArgs? e)
        {
            SetActiveButton(btnActivationTab);
            if (sender != null) AppLogger.Write("📂 Открыта вкладка: Активация");
            if (_activationTab == null) _activationTab = new ActivationTab();
            MainFrame.Content = (_activationTab);
            UpdateMascot("activation");
        }

        private void NavigateToAbout(object? sender, RoutedEventArgs? e)
        {
            SetActiveButton(btnAboutTab);
            AppLogger.Write("📂 Открыта вкладка: О программе");
            if (_aboutTab == null) _aboutTab = new AboutTab();
            MainFrame.Content = (_aboutTab);
            UpdateMascot("about");
        }

        /// <summary>
        /// Индикатор соединения в боковой панели. Текст и цвет были захардкожены в
        /// разметке («Интернет доступен», зелёный) и не менялись никогда — при
        /// пропаже сети или включённом принудительном офлайне панель продолжала
        /// уверять, что интернет есть, хотя вкладки в этот же момент скрывались.
        /// Формулировки и цветовая логика повторяют индикатор на вкладке
        /// «Настройки» (<see cref="ViewModels.SystemViewModel.UpdateConnectivityStatus"/>),
        /// только короче — пилюля в панели узкая.
        /// </summary>
        private void UpdateConnectionIndicator()
        {
            bool offlineForced = ProfileService.Current.OfflineMode;
            bool onlineForced  = ProfileService.Current.ForceOnlineMode;
            bool online        = ConnectivityMonitor.IsOnline;

            string text;
            string brushKey;
            if (offlineForced)
            {
                text = "Принудительный офлайн";
                brushKey = "StatusWarning";
            }
            else if (!online && onlineForced)
            {
                text = "Онлайн-режим принудительно";
                brushKey = "StatusWarning";
            }
            else if (!online)
            {
                text = "Интернет недоступен";
                brushKey = "StatusDanger";
            }
            else
            {
                text = "Интернет доступен";
                brushKey = "StatusSuccess";
            }

            var brush = (Brush)FindResource(brushKey);
            txtConnectionStatus.Text = text;
            txtConnectionStatus.Foreground = brush;
            dotConnectionStatus.Fill = brush;
        }

        public void UpdateTabVisibility()
        {
            bool online    = ConnectivityMonitor.IsEffectivelyOnline && !ProfileService.Current.OfflineMode;

            // Индикатор соединения обновляем здесь же: этот метод вызывается на смену
            // статуса сети (OnConnectivityChanged), при старте и при переключении
            // офлайн/принудительно-онлайн на вкладке «Настройки» — ровно тот же набор
            // событий, от которого зависит и текст индикатора.
            UpdateConnectionIndicator();

            // Вкладки, работающие только при наличии сети
            btnOfficeTab.Visibility     = online ? Visibility.Visible : Visibility.Collapsed;
            btnActivationTab.Visibility = online ? Visibility.Visible : Visibility.Collapsed;
            btnNetworkTab.Visibility    = online ? Visibility.Visible : Visibility.Collapsed;

            btnHistoryTab.Visibility = Visibility.Visible;

            if (!online)
            {
                string reason = !ConnectivityMonitor.IsEffectivelyOnline
                    ? "🔌 Нет интернета — часть вкладок скрыта"
                    : "🔌 Офлайн режим — часть вкладок скрыта";
                AppLogger.Write(reason);

                if (_currentTab is "office" or "activation" or "network")
                    NavigateToCatalog(null, null);
            }

        }

        // ── Debloater tab ─────────────────────────────────────────────────────────

        private void NavigateToDebloater(object? sender, RoutedEventArgs? e)
        {
            SetActiveButton(btnDebloaterTab);
            AppLogger.Write("📂 Открыта вкладка: Очистка");
            if (_debloaterTab == null) _debloaterTab = new DebloaterTab();
            MainFrame.Content = (_debloaterTab);
            UpdateMascot("debloater");
        }

        /// <summary>
        /// Гарантирует создание вкладки Debloater (без перехода на неё) и возвращает её —
        /// нужно снапшотам конфигурации, чтобы прочитать/применить твики даже если
        /// пользователь ни разу не открывал вкладку «Очистка» за эту сессию.
        /// </summary>
        public DebloaterTab EnsureDebloaterTab()
        {
            if (_debloaterTab == null) _debloaterTab = new DebloaterTab();
            return _debloaterTab;
        }

        // ── History tab ───────────────────────────────────────────────────────────

        private void NavigateToHistory(object? sender, RoutedEventArgs? e)
        {
            SetActiveButton(btnHistoryTab);
            AppLogger.Write("📂 Открыта вкладка: История");
            if (_historyTab == null) _historyTab = new HistoryTab();
            MainFrame.Content = (_historyTab);
            _ = _historyTab.RefreshAsync();
            UpdateMascot("history");
        }

        // ── Трей ──────────────────────────────────────────────────────────────────

        private void ShowFromTray()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        }

        private void ForceExit()
        {
            _tray.Dispose();
            ConnectivityMonitor.Stop();
            Application.Current.Shutdown();
        }

        // Единственный обработчик Closing (подключён в XAML) — объединяет сворачивание
        // в трей, предупреждение об активной установке и окно отзыва на prerelease.
        private void Window_Closing_Extended(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // При сворачивании в трей окно не закрывается — установка продолжается,
            // предупреждение и окно отзыва не нужны. Если иконку создать не удалось
            // (см. TrayIconController.Initialize) — сворачивать некуда, окно должно
            // закрыться штатно, иначе процесс зависает без окна и без иконки.
            if (ProfileService.Current.MinimizeToTray && _tray.IsAvailable)
            {
                e.Cancel = true;
                Hide();
                _tray.ShowBalloon(2000, "Ven4Tools",
                    "Приложение свёрнуто в трей. Двойной клик для открытия.");
                return;
            }

            // Предупреждение при закрытии во время активной установки
            if (_catalogTab?.IsInstalling == true)
            {
                var res = MessageBox.Show(
                    "Идёт установка приложений.\n\nЗакрыть программу и прервать установку?",
                    "Установка в процессе",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Warning);

                // OK — закрыть, Отмена — продолжить работу
                if (res != MessageBoxResult.OK)
                {
                    e.Cancel = true;
                    return;
                }
            }

            // На prerelease-канале перед выходом один раз показываем окно отзыва;
            // после его закрытия Close() вызывается повторно и приложение завершается.
            // В параноидальном режиме окно не показываем вовсе: FeedbackService в нём
            // ничего не отправляет, а написанный отзыв лёг бы в pending_feedback.json
            // и ушёл бы на сервер позже — при первом же старте с выключенным режимом.
            if (ChannelService.IsPreRelease && !_feedbackShown && !ProfileService.Current.ParanoidMode)
            {
                e.Cancel = true;
                _feedbackShown = true;
                var fw = new Views.FeedbackWindow { Owner = this };
                fw.Closed += (_, _) => Close();
                fw.Show();
                return;
            }

            _tray.Dispose();
            ConnectivityMonitor.Stop();
        }

        // ── Подключение XAML-событий к контроллерам ───────────────────────────────

        private void MainArea_DragEnter(object sender, DragEventArgs e) => _drop.DragEnter(e);

        private void MainArea_DragOver(object sender, DragEventArgs e) => _drop.DragOver(e);

        private void MainArea_DragLeave(object sender, DragEventArgs e) => _drop.DragLeave(e);

        private void MainArea_Drop(object sender, DragEventArgs e) => _drop.Drop(e);

        private void BtnClearGlobalLog_Click(object sender, RoutedEventArgs e) => _globalLog.Clear();

        private void CopyGlobalLog_Click(object sender, RoutedEventArgs e) => _globalLog.CopySelectedOrAll();

        // ── Прочее ────────────────────────────────────────────────────────────────

        private void UpdateMascot(string tabName)
        {
            _currentTab = tabName;
            _mascot.Show(tabName);
        }

        private void SetActiveButton(Button activeButton)
        {
            var buttons = new[] { btnCatalogTab, btnInstalledTab, btnSystemTab, btnDiagnosticsTab, btnBenchmarkTab, btnOfficeTab, btnActivationTab, btnAboutTab, btnNetworkTab, btnHistoryTab, btnDebloaterTab, btnWindowsUpdateTab };
            foreach (var btn in buttons)
            {
                if (btn != null) btn.Style = (Style)FindResource("NavButtonStyle");
            }
            activeButton.Style = (Style)FindResource("ActiveNavButtonStyle");
            MotionService.Pulse(activeButton, 1.02, 140);
            Dispatcher.BeginInvoke(new Action(() => MotionService.SlideIn(MainFrame, 6, 160)),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private bool IsRunAsAdmin()
        {
            var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }

        private void RestartAsAdmin()
        {
            var exeName = Process.GetCurrentProcess().MainModule?.FileName;
            if (exeName != null)
            {
                // Освобождаем мьютекс единственного экземпляра ДО запуска повышенной
                // копии — иначе она может увидеть его ещё занятым и выйти как
                // «уже запущено», и не останется ни одного рабочего экземпляра.
                App.ReleaseSingleInstanceMutex();
                try
                {
                    Process.Start(new ProcessStartInfo { FileName = exeName, UseShellExecute = true, Verb = "runas" });
                }
                catch
                {
                    // Пользователь отклонил UAC — повышенная копия не стартовала.
                    // Возвращаем мьютекс, чтобы состояние единственного экземпляра
                    // оставалось согласованным до завершения процесса.
                    App.ReacquireSingleInstanceMutex();
                }
            }
            // Конструктор окна вышел до инициализации вкладок, а окно уже показано —
            // без прав администратора клиент неработоспособен, поэтому завершаемся
            // в любом случае.
            Application.Current.Shutdown();
        }

        private void ShowCategorySelectionIfNeeded()
        {
            if (_categorySelectionShown) return;
            if (ProfileService.Current.HasSelectedCategory) return;
            _categorySelectionShown = true;

            var win = new CategorySelectionWindow { Owner = this };
            if (win.ShowDialog() != true)
                _categorySelectionShown = false;
        }
    }
}
