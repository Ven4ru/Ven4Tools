using System.Threading.Tasks;
using System.Windows.Media;
using Ven4Tools.Helpers;

namespace Ven4Tools.ViewModels
{
    /// <summary>
    /// Одна строка статуса (пинг-хост или сервис) — текст задержки/статуса,
    /// иконка и её цвет. Общий тип для 4 строк пинга и 5 строк проверки сервисов —
    /// в оригинальном code-behind они обновлялись почти идентичной логикой
    /// (<c>SetPingRow</c> и инлайн-лямбда в <c>RunServicesAsync</c>), здесь эта
    /// логика едина (см. <see cref="NetworkViewModel.SetRow"/>).
    /// </summary>
    public sealed class NetworkCheckResult : ViewModelBase
    {
        private string _text;
        private string _iconText = "⬜";
        // Оригинальные txtPingIcon*/txtSvc* не задавали Foreground в XAML явно —
        // цвет наследовался от глобального Style TargetType="TextBlock" (App.xaml:60),
        // который ставит DynamicResource TextPrimary. Явный биндинг на IconBrush
        // заменяет этот неявный канал, поэтому дефолт берётся из того же ресурса
        // с фолбэком на замороженный (frozen, потокобезопасный) Brushes.White.
        private Brush _iconBrush = BrushResolver.Resolve("TextPrimary");

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
    }

    /// <summary>
    /// ViewModel вкладки «Сеть». Логика перенесена из code-behind при MVVM-миграции
    /// (2026-08-25, пятая вкладка после Debloater/History/About/Activation) без
    /// изменения поведения.
    /// Разбит на partial-файлы по образцу SystemViewModel.*/BenchmarkViewModel.*:
    /// здесь ядро (флаги занятости, команды, полная диагностика), отдельно
    /// .Adapters.cs, .Checks.cs (пинг/сервисы/внешний IP/DNS) и .Reset.cs.
    /// </summary>
    public sealed partial class NetworkViewModel : ViewModelBase
    {
        // ── Состояние занятости ──────────────────────────────────────────────

        // Сеттеры busy-флагов диагностики — internal (а не private) ради тестов:
        // они позволяют собрать состояние «занято» без реальных сетевых вызовов
        // и проверить ResetDiagnosticFlags. Доступ открыт только сборке тестов
        // через InternalsVisibleTo (Properties/AssemblyInfo.cs).
        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            internal set { if (SetField(ref _isBusy, value)) RaiseAllCanExecuteChanged(); }
        }

        private bool _isPinging;
        public bool IsPinging
        {
            get => _isPinging;
            internal set { if (SetField(ref _isPinging, value)) PingCommand.RaiseCanExecuteChanged(); }
        }

        private bool _isCheckingServices;
        public bool IsCheckingServices
        {
            get => _isCheckingServices;
            internal set { if (SetField(ref _isCheckingServices, value)) CheckServicesCommand.RaiseCanExecuteChanged(); }
        }

        private bool _isGettingIp;
        public bool IsGettingIp
        {
            get => _isGettingIp;
            internal set { if (SetField(ref _isGettingIp, value)) GetIpCommand.RaiseCanExecuteChanged(); }
        }

        private bool _isCheckingDns;
        public bool IsCheckingDns
        {
            get => _isCheckingDns;
            internal set { if (SetField(ref _isCheckingDns, value)) CheckDnsCommand.RaiseCanExecuteChanged(); }
        }

        private bool _isResettingNetwork;
        public bool IsResettingNetwork
        {
            get => _isResettingNetwork;
            internal set { if (SetField(ref _isResettingNetwork, value)) ResetNetworkCommand.RaiseCanExecuteChanged(); }
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
    }
}
