using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Ven4Tools.Services;

namespace Ven4Tools.ViewModels
{
    /// <summary>
    /// Одна строка статуса (пинг-хост или сервис) — текст задержки/статуса,
    /// иконка и её цвет. Общий тип для 4 строк пинга и 5 строк проверки сервисов —
    /// в оригинальном code-behind они обновлялись почти идентичной логикой
    /// (<c>SetPingRow</c> и инлайн-лямбда в <c>RunServicesAsync</c>), здесь эта
    /// логика едина (см. <see cref="NetworkViewModel.SetRow"/>).
    /// </summary>
    public sealed class NetworkCheckResult : INotifyPropertyChanged
    {
        private string _text;
        private string _iconText = "⬜";
        private Brush _iconBrush = ResolveDefaultBrush();

        /// <summary>
        /// Начальный текст строки. Оригинальный XAML задавал разные дефолты:
        /// <c>txtPing1..4 Text="—"</c>, но <c>txtSvcMs1..5 Text=""</c> (пусто),
        /// поэтому дефолт параметризован.
        /// </summary>
        public NetworkCheckResult(string initialText = "—")
        {
            _text = initialText;
        }

        public string Text
        {
            get => _text;
            set => SetField(ref _text, value);
        }

        public string IconText
        {
            get => _iconText;
            set => SetField(ref _iconText, value);
        }

        public Brush IconBrush
        {
            get => _iconBrush;
            set => SetField(ref _iconBrush, value);
        }

        // Оригинальные txtPingIcon*/txtSvc* не задавали Foreground в XAML явно —
        // цвет наследовался от глобального Style TargetType="TextBlock" (App.xaml:60),
        // который ставит DynamicResource TextPrimary. Явный биндинг на IconBrush
        // заменяет этот неявный канал, поэтому дефолт вычисляется тем же способом,
        // что уже проверен ревью ActivationViewModel — TryFindResource с фолбэком
        // на замороженный (frozen, потокобезопасный) Brushes.White.
        internal static Brush ResolveDefaultBrush() =>
            (Application.Current?.TryFindResource("TextPrimary") as Brush) ?? Brushes.White;

        public event PropertyChangedEventHandler? PropertyChanged;

        private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// ViewModel вкладки «Сеть». Логика перенесена из code-behind при MVVM-миграции
    /// (2026-08-25, пятая вкладка после Debloater/History/About/Activation) без
    /// изменения поведения — см. docs/superpowers/specs/2026-08-25-networktab-mvvm-design.md.
    /// </summary>
    public sealed class NetworkViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        // ── Адаптеры ─────────────────────────────────────────────────────────

        private IReadOnlyList<AdapterInfo> _adapters = Array.Empty<AdapterInfo>();
        public IReadOnlyList<AdapterInfo> Adapters
        {
            get => _adapters;
            private set => SetField(ref _adapters, value);
        }

        private bool _adaptersEmpty;
        public bool AdaptersEmpty
        {
            get => _adaptersEmpty;
            private set => SetField(ref _adaptersEmpty, value);
        }

        // ── Пинг ─────────────────────────────────────────────────────────────

        public NetworkCheckResult Ping1 { get; } = new();
        public NetworkCheckResult Ping2 { get; } = new();
        public NetworkCheckResult Ping3 { get; } = new();
        public NetworkCheckResult Ping4 { get; } = new();

        // ── Доступность сервисов ─────────────────────────────────────────────

        // Оригинальный XAML давал строкам сервисов пустой Text (txtSvcMs1..5 Text=""),
        // в отличие от строк пинга с "—" — сохраняем это различие.
        public NetworkCheckResult Svc1 { get; } = new(initialText: "");
        public NetworkCheckResult Svc2 { get; } = new(initialText: "");
        public NetworkCheckResult Svc3 { get; } = new(initialText: "");
        public NetworkCheckResult Svc4 { get; } = new(initialText: "");
        public NetworkCheckResult Svc5 { get; } = new(initialText: "");

        // ── Внешний IP / DNS ─────────────────────────────────────────────────

        private string _publicIpText = "не определён";
        public string PublicIpText
        {
            get => _publicIpText;
            private set => SetField(ref _publicIpText, value);
        }

        private string _dnsResultText = "";
        public string DnsResultText
        {
            get => _dnsResultText;
            private set => SetField(ref _dnsResultText, value);
        }

        private bool _dnsResultVisible;
        public bool DnsResultVisible
        {
            get => _dnsResultVisible;
            private set => SetField(ref _dnsResultVisible, value);
        }

        // ── Состояние занятости ──────────────────────────────────────────────

        // Сеттеры busy-флагов диагностики — internal (а не private) ради тестов:
        // они позволяют собрать состояние «занято» без реальных сетевых вызовов
        // и проверить ResetDiagnosticFlags. Доступ открыт только сборке тестов
        // через InternalsVisibleTo (Properties/AssemblyInfo.cs).
        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            internal set { SetField(ref _isBusy, value); RaiseAllCanExecuteChanged(); }
        }

        private bool _isPinging;
        public bool IsPinging
        {
            get => _isPinging;
            internal set { SetField(ref _isPinging, value); PingCommand.RaiseCanExecuteChanged(); }
        }

        private bool _isCheckingServices;
        public bool IsCheckingServices
        {
            get => _isCheckingServices;
            internal set { SetField(ref _isCheckingServices, value); CheckServicesCommand.RaiseCanExecuteChanged(); }
        }

        private bool _isGettingIp;
        public bool IsGettingIp
        {
            get => _isGettingIp;
            internal set { SetField(ref _isGettingIp, value); GetIpCommand.RaiseCanExecuteChanged(); }
        }

        private bool _isCheckingDns;
        public bool IsCheckingDns
        {
            get => _isCheckingDns;
            internal set { SetField(ref _isCheckingDns, value); CheckDnsCommand.RaiseCanExecuteChanged(); }
        }

        private bool _isResettingNetwork;
        public bool IsResettingNetwork
        {
            get => _isResettingNetwork;
            private set { SetField(ref _isResettingNetwork, value); ResetNetworkCommand.RaiseCanExecuteChanged(); }
        }

        private string _runAllButtonText = "🔍 Запустить полную диагностику";
        public string RunAllButtonText
        {
            get => _runAllButtonText;
            private set => SetField(ref _runAllButtonText, value);
        }

        private void RaiseAllCanExecuteChanged()
        {
            RunAllCommand.RaiseCanExecuteChanged();
            RefreshAdaptersCommand.RaiseCanExecuteChanged();
            PingCommand.RaiseCanExecuteChanged();
            CheckServicesCommand.RaiseCanExecuteChanged();
            GetIpCommand.RaiseCanExecuteChanged();
            CheckDnsCommand.RaiseCanExecuteChanged();
            ResetNetworkCommand.RaiseCanExecuteChanged();
        }

        // ── Команды ──────────────────────────────────────────────────────────

        public RelayCommand RunAllCommand { get; }
        public RelayCommand RefreshAdaptersCommand { get; }
        public RelayCommand PingCommand { get; }
        public RelayCommand CheckServicesCommand { get; }
        public RelayCommand GetIpCommand { get; }
        public RelayCommand CheckDnsCommand { get; }
        public RelayCommand ResetNetworkCommand { get; }

        public NetworkViewModel()
        {
            RunAllCommand          = RelayCommand.FromAsync(_ => RunAllAsync(),     _ => !IsBusy);
            RefreshAdaptersCommand = new RelayCommand(_ => RefreshAdapters(),       _ => !IsBusy);
            PingCommand             = RelayCommand.FromAsync(_ => RunPingAsync(),     _ => !IsBusy && !IsPinging);
            CheckServicesCommand    = RelayCommand.FromAsync(_ => RunServicesAsync(), _ => !IsBusy && !IsCheckingServices);
            GetIpCommand            = RelayCommand.FromAsync(_ => RunGetIpAsync(),    _ => !IsBusy && !IsGettingIp);
            CheckDnsCommand         = RelayCommand.FromAsync(_ => RunDnsAsync(),      _ => !IsBusy && !IsCheckingDns);
            ResetNetworkCommand     = RelayCommand.FromAsync(_ => RunResetNetworkAsync(), _ => !IsBusy && !IsResettingNetwork);
        }

        // ── Полная диагностика ───────────────────────────────────────────────

        private async Task RunAllAsync()
        {
            // Явный гейт реентерабельности (эквивалент "if (_busy) return;" оригинального
            // code-behind). Одного CanExecute мало: CommandManager.InvalidateRequerySuggested()
            // публикует перезапрос доступности с приоритетом DispatcherPriority.Background,
            // который НИЖЕ приоритета обработки ввода — между присвоением флага и реальным
            // отключением кнопки остаётся окно, в которое проходит повторный клик.
            if (IsBusy) return;
            IsBusy = true;
            RunAllButtonText = "⏳ Диагностика...";
            try
            {
                RefreshAdapters();
                await RunPingAsync();
                await RunServicesAsync();
                await RunGetIpAsync();
                await RunDnsAsync();
            }
            finally
            {
                ResetDiagnosticFlags();
            }
        }

        /// <summary>
        /// Безусловно возвращает все флаги диагностики в исходное состояние.
        /// Оригинал (<c>SetDiagnosticButtonsEnabled(true)</c> в <c>finally</c>) безусловно
        /// возвращал ВСЕ 7 кнопок в <c>IsEnabled=true</c>, даже если внутренние методы
        /// не сбросили свой busy-флаг сами (условие <c>if (!_busy)</c> внутри них было
        /// ложным, пока эта диагностика ещё выполнялась). Этот безусловный сброс —
        /// точный эквивалент, см. Global Constraints плана. Удаление любой строки
        /// отсюда навсегда заблокирует соответствующую кнопку после первой полной
        /// диагностики, поэтому метод выделен отдельно и покрыт юнит-тестом.
        /// </summary>
        internal void ResetDiagnosticFlags()
        {
            IsBusy = false;
            IsPinging = false;
            IsCheckingServices = false;
            IsGettingIp = false;
            IsCheckingDns = false;
            RunAllButtonText = "🔍 Запустить полную диагностику";
        }

        // ── Адаптеры ─────────────────────────────────────────────────────────

        private void RefreshAdapters()
        {
            var adapters = DiagnosticsService.GetAdapters();
            Adapters = adapters;
            AdaptersEmpty = adapters.Count == 0;
            AppLogger.Write($"[Сеть] Адаптеров: {adapters.Count}");
        }

        // ── Пинг ─────────────────────────────────────────────────────────────

        private async Task RunPingAsync()
        {
            // Гейт реентерабельности — см. пояснение в RunAllAsync. Проверяется СВОЙ флаг,
            // а не IsBusy: при вызове из полной диагностики IsPinging здесь ещё false,
            // так что штатный сценарий RunAll не обрывается.
            if (IsPinging) return;
            IsPinging = true;
            // Параноидальный режим обещает блокировать ВСЕ исходящие запросы, кроме
            // загрузки каталога и установки. Пинг сторонних хостов раскрывает IP —
            // пропускаем, чтобы не нарушать это обещание (внешний IP тут уже гейтится).
            if (ProfileService.Current.ParanoidMode)
            {
                SetRow(Ping1, "отключено", null);
                SetRow(Ping2, "отключено", null);
                SetRow(Ping3, "отключено", null);
                SetRow(Ping4, "отключено", null);
                AppLogger.Write("[Сеть] Пинг пропущен: параноидальный режим");
                if (!IsBusy) IsPinging = false;
                return;
            }
            SetRow(Ping1, "...", null);
            SetRow(Ping2, "...", null);
            SetRow(Ping3, "...", null);
            SetRow(Ping4, "...", null);

            var hosts = new[] { "1.1.1.1", "8.8.8.8", "google.com", "ven4tools.ru" };
            var targets = new[] { Ping1, Ping2, Ping3, Ping4 };

            var tasks = new List<Task>();
            for (int i = 0; i < hosts.Length; i++)
            {
                var host = hosts[i];
                var row = targets[i];
                tasks.Add(Task.Run(async () =>
                {
                    var r = await DiagnosticsService.PingHostAsync(host);
                    Application.Current.Dispatcher.Invoke(() => SetRow(row, r.Display, r.Reachable));
                    AppLogger.Write($"[Сеть] Пинг {host}: {r.Display}");
                }));
            }
            await Task.WhenAll(tasks);
            // Во время полной диагностики флаг разблокирует RunAllAsync в finally.
            if (!IsBusy) IsPinging = false;
        }

        internal static void SetRow(NetworkCheckResult row, string text, bool? ok)
        {
            row.Text = text;
            if (ok == null) { row.IconText = "⬜"; row.IconBrush = Brushes.Gray; return; }
            if (ok == true) { row.IconText = "✅"; row.IconBrush = new SolidColorBrush(Color.FromRgb(74, 222, 128)); }
            else            { row.IconText = "❌"; row.IconBrush = new SolidColorBrush(Colors.LightCoral); }
        }

        // ── Доступность сервисов ─────────────────────────────────────────────

        private async Task RunServicesAsync()
        {
            // Гейт реентерабельности — см. пояснение в RunAllAsync.
            if (IsCheckingServices) return;
            IsCheckingServices = true;
            var rows = new[] { Svc1, Svc2, Svc3, Svc4, Svc5 };
            // Параноидальный режим: HEAD-запросы к сторонним сервисам раскрывают IP —
            // пропускаем ради соблюдения обещания режима (см. RunPingAsync).
            if (ProfileService.Current.ParanoidMode)
            {
                foreach (var row in rows)
                {
                    row.IconText = "🚫";
                    row.IconBrush = Brushes.Gray;
                    row.Text = "отключено";
                }
                AppLogger.Write("[Сеть] Проверка сервисов пропущена: параноидальный режим");
                if (!IsBusy) IsCheckingServices = false;
                return;
            }
            foreach (var row in rows) { row.IconText = "⏳"; row.IconBrush = Brushes.Gray; }

            var checks = new[]
            {
                ("Google",     "https://www.google.com"),
                ("YouTube",    "https://www.youtube.com"),
                ("Discord",    "https://discord.com"),
                ("Cloudflare", "https://www.cloudflare.com"),
                ("GitHub",     "https://github.com"),
            };

            var tasks = new List<Task>();
            for (int i = 0; i < checks.Length; i++)
            {
                var (name, url) = checks[i];
                var row = rows[i];
                tasks.Add(Task.Run(async () =>
                {
                    var r = await DiagnosticsService.CheckServiceAsync(name, url);
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        row.IconText = r.Available ? "✅" : "❌";
                        row.IconBrush = r.Available
                            ? new SolidColorBrush(Color.FromRgb(74, 222, 128))
                            : new SolidColorBrush(Colors.LightCoral);
                        row.Text = r.Available ? $"{r.Ms} мс" : "таймаут";
                    });
                    AppLogger.Write($"[Сеть] {name}: {(r.Available ? "✅" : "❌")} {r.Ms}мс");
                }));
            }
            await Task.WhenAll(tasks);
            if (!IsBusy) IsCheckingServices = false;
        }

        // ── Внешний IP ───────────────────────────────────────────────────────

        private async Task RunGetIpAsync()
        {
            // Гейт реентерабельности — см. пояснение в RunAllAsync.
            if (IsGettingIp) return;
            IsGettingIp = true;
            // Параноидальный режим: сам смысл этого запроса — раскрыть внешний IP
            // стороннему echo-сервису, единственная функция которого — раскрыть его.
            if (ProfileService.Current.ParanoidMode)
            {
                PublicIpText = "отключено (параноидальный режим)";
                AppLogger.Write("[Сеть] Запрос внешнего IP пропущен: параноидальный режим");
                if (!IsBusy) IsGettingIp = false;
                return;
            }
            PublicIpText = "определяется...";
            try
            {
                var ip = await DiagnosticsService.GetPublicIpAsync();
                PublicIpText = ip;
                // Само ЗНАЧЕНИЕ внешнего IP в журнал не пишем — пишем только факт и исход.
                AppLogger.Write(ip == "не определён"
                    ? "[Сеть] Внешний IP не определён"
                    : "[Сеть] Внешний IP определён");
            }
            finally { if (!IsBusy) IsGettingIp = false; }
        }

        // ── DNS ──────────────────────────────────────────────────────────────

        private async Task RunDnsAsync()
        {
            // Гейт реентерабельности — см. пояснение в RunAllAsync.
            if (IsCheckingDns) return;
            IsCheckingDns = true;
            DnsResultVisible = true;
            // Параноидальный режим: DNS-резолюция через внешний резолвер тоже сетевой
            // запрос вне разрешённых исключений — пропускаем (см. RunPingAsync).
            if (ProfileService.Current.ParanoidMode)
            {
                DnsResultText = "Отключено (параноидальный режим)";
                AppLogger.Write("[Сеть] DNS-проверка пропущена: параноидальный режим");
                if (!IsBusy) IsCheckingDns = false;
                return;
            }
            DnsResultText = "Проверка DNS...";
            try
            {
                var result = await DiagnosticsService.CheckDnsAsync("google.com");
                DnsResultText = result;
                AppLogger.Write("[Сеть] DNS проверка завершена");
            }
            catch (Exception ex) { DnsResultText = $"Ошибка: {ex.Message}"; }
            finally { if (!IsBusy) IsCheckingDns = false; }
        }

        // ── Сброс сети ───────────────────────────────────────────────────────

        private async Task RunResetNetworkAsync()
        {
            // Гейт реентерабельности — см. пояснение в RunAllAsync. Здесь он особенно
            // важен: без него повторный клик до отключения кнопки покажет второй диалог
            // подтверждения и может запустить вторую цепочку netsh параллельно.
            if (IsResettingNetwork) return;
            var confirm = MessageBox.Show(
                "Сброс сетевых настроек:\n\n" +
                "• netsh winsock reset\n• netsh int ip reset\n• ipconfig /release\n• ipconfig /renew\n\n" +
                "Потребуются права администратора и перезагрузка.\n\nПродолжить?",
                "Сброс сети", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            IsResettingNetwork = true;
            try
            {
                AppLogger.Write("[Сеть] Запуск сброса сетевых настроек...");
                // Приложение уже работает с правами администратора (перезапуск через UAC
                // в MainWindow), поэтому runas не нужен — запускаем скрыто и перенаправляем
                // вывод команд в лог-панель вместо отдельного окна консоли.
                var psi = new ProcessStartInfo
                {
                    FileName  = TrustedExecutablePaths.CmdExe,
                    Arguments = "/c netsh winsock reset & netsh int ip reset & " +
                                "ipconfig /release & ipconfig /renew",
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                    WindowStyle            = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true
                };
                int exitCode = -1;
                using var p = Process.Start(psi);
                if (p != null)
                {
                    var stdoutTask = p.StandardOutput.ReadToEndAsync();
                    var stderrTask = p.StandardError.ReadToEndAsync();
                    await p.WaitForExitAsync();
                    exitCode = p.ExitCode;

                    foreach (var line in (await stdoutTask).Split('\n'))
                    {
                        var t = line.Trim();
                        if (!string.IsNullOrWhiteSpace(t)) AppLogger.Write($"[Сеть] {t}");
                    }
                    var err = (await stderrTask).Trim();
                    if (!string.IsNullOrWhiteSpace(err)) AppLogger.Write($"[Сеть] ⚠ {err}");
                }

                // Цепочка команд через «&» возвращает код последней, ненулевой код
                // означает, что часть сброса не удалась (нет прав, DHCP и т.п.).
                if (exitCode == 0)
                {
                    AppLogger.Write("[Сеть] Сброс завершён");
                    MessageBox.Show("Перезагрузите компьютер для применения изменений.",
                        "Готово", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    AppLogger.Write($"[Сеть] ⚠ Сброс завершился с кодом {exitCode} — часть команд могла не выполниться");
                    MessageBox.Show(
                        $"Сброс сетевых настроек завершился с ошибкой (код {exitCode}). Часть команд могла не выполниться.\n\n" +
                        "Запустите приложение от имени администратора и попробуйте ещё раз. Подробности — в логах.",
                        "Сброс не завершён", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[Сеть] Ошибка сброса: {ex.Message}");
                MessageBox.Show("Не удалось сбросить сетевые настройки. Запустите приложение от имени администратора и попробуйте ещё раз.",
                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            // Оригинал сбрасывает IsEnabled безусловно (без "if (!_busy)") — сброс сети
            // не вызывается из RunAllAsync, так что busy-гонки здесь нет по построению.
            finally { IsResettingNetwork = false; }
        }
    }
}
