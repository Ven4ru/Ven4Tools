using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Ven4Tools.Services;

namespace Ven4Tools.ViewModels
{
    /// <summary>
    /// Блок «Датчики»: температуры, частоты, загрузка и вентиляторы. Опрос раз в
    /// 2 секунды и только пока вкладка на экране — вне её датчики не трогаются.
    /// Драйвер датчиков процессора (PawnIO) ставится и удаляется только кнопкой.
    /// </summary>
    public sealed partial class DiagnosticsViewModel
    {
        private DispatcherTimer? _sensorTimer;
        private bool _sensorReadInFlight;
        private IReadOnlyList<SensorCard> _lastSensorCards = Array.Empty<SensorCard>();

        private IReadOnlyList<SensorCard> _sensorCards = Array.Empty<SensorCard>();
        public IReadOnlyList<SensorCard> SensorCards { get => _sensorCards; private set => SetField(ref _sensorCards, value); }

        private string _sensorStatusText = "Датчики опрашиваются…";
        public string SensorStatusText { get => _sensorStatusText; private set => SetField(ref _sensorStatusText, value); }

        private string _sensorDriverText = "";
        public string SensorDriverText { get => _sensorDriverText; private set => SetField(ref _sensorDriverText, value); }

        private bool _showInstallSensorDriver;
        public bool ShowInstallSensorDriver { get => _showInstallSensorDriver; private set => SetField(ref _showInstallSensorDriver, value); }

        private bool _showRemoveSensorDriver;
        public bool ShowRemoveSensorDriver { get => _showRemoveSensorDriver; private set => SetField(ref _showRemoveSensorDriver, value); }

        private bool _isChangingSensorDriver;
        public bool IsChangingSensorDriver
        {
            get => _isChangingSensorDriver;
            internal set
            {
                if (!SetField(ref _isChangingSensorDriver, value)) return;
                InstallSensorDriverCommand.RaiseCanExecuteChanged();
                RemoveSensorDriverCommand.RaiseCanExecuteChanged();
            }
        }

        public RelayCommand InstallSensorDriverCommand { get; private set; } = null!;
        public RelayCommand RemoveSensorDriverCommand { get; private set; } = null!;

        /// <summary>Венчик в оверлее: жарко, нормально или холодно. Запоминается в профиле.</summary>
        public bool ShowOverlayMascot
        {
            get => ProfileService.Current.OverlayShowMascot;
            set
            {
                if (ProfileService.Current.OverlayShowMascot == value) return;
                ProfileService.Current.OverlayShowMascot = value;
                ProfileService.Save();
                OnPropertyChanged();
            }
        }

        // ── Отзывы об экспериментальном модуле ───────────────────────────────────
        // Модуль вышел без драйвера датчиков; отзывы покажут, нужна ли полная версия.

        public RelayCommand ModuleFeedbackGitHubCommand { get; private set; } = null!;
        public RelayCommand ModuleFeedbackSiteCommand { get; private set; } = null!;

        internal static string ModuleFeedbackGitHubUrl(string version) =>
            "https://github.com/Ven4ru/Ven4Tools/issues/new?template=module_feedback.yml" +
            "&title=" + Uri.EscapeDataString("Отзыв: датчики и оверлей (эксперимент)") +
            "&version=" + Uri.EscapeDataString(version);

        internal const string ModuleFeedbackSiteUrl = "https://ven4tools.ru/#scene-reviews";

        private static void OpenModuleFeedback(string url, string where)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = url, UseShellExecute = true });
                AppLogger.Write($"💬 Открыт отзыв о модуле датчиков и оверлея: {where}");
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "DiagnosticsViewModel.OpenModuleFeedback");
                MessageBox.Show("Не удалось открыть браузер: " + ex.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public RelayCommand ToggleOverlayCommand { get; private set; } = null!;
        public RelayCommand InstallFpsCounterCommand { get; private set; } = null!;

        private string _overlayButtonText = "";
        public string OverlayButtonText { get => _overlayButtonText; private set => SetField(ref _overlayButtonText, value); }

        private bool _showInstallFpsCounter;
        public bool ShowInstallFpsCounter { get => _showInstallFpsCounter; private set => SetField(ref _showInstallFpsCounter, value); }

        private bool _isInstallingFpsCounter;
        public bool IsInstallingFpsCounter
        {
            get => _isInstallingFpsCounter;
            private set { if (SetField(ref _isInstallingFpsCounter, value)) InstallFpsCounterCommand.RaiseCanExecuteChanged(); }
        }

        private void InitSensorCommands()
        {
            InstallSensorDriverCommand = RelayCommand.FromAsync(_ => InstallSensorDriverAsync(), _ => !IsChangingSensorDriver);
            RemoveSensorDriverCommand = RelayCommand.FromAsync(_ => RemoveSensorDriverAsync(), _ => !IsChangingSensorDriver);
            ToggleOverlayCommand = new RelayCommand(_ => OverlayService.Instance.Toggle());
            ModuleFeedbackGitHubCommand = new RelayCommand(_ =>
                OpenModuleFeedback(ModuleFeedbackGitHubUrl(ChannelService.InstalledVersion), "GitHub"));
            ModuleFeedbackSiteCommand = new RelayCommand(_ => OpenModuleFeedback(ModuleFeedbackSiteUrl, "сайт"));
            InstallFpsCounterCommand = RelayCommand.FromAsync(_ => InstallFpsCounterAsync(), _ => !IsInstallingFpsCounter);
            // Оверлей переключается и сочетанием клавиш — подпись кнопки должна это видеть.
            // Отписки нет: экземпляр ViewModel вкладки один на весь сеанс.
            OverlayService.Instance.StateChanged += RefreshOverlayState;
            RefreshSensorDriverState();
            RefreshOverlayState();
        }

        private void RefreshOverlayState()
        {
            OverlayButtonText = !OverlayService.Instance.IsVisible
                ? $"🎮 Показать оверлей поверх игр ({OverlayService.HotkeyText})"
                : !OverlayService.Instance.IsExtended
                    ? $"🎮 Расширенный оверлей ({OverlayService.HotkeyText})"
                    : $"🎮 Скрыть оверлей ({OverlayService.HotkeyText})";
            ShowInstallFpsCounter = PresentMonService.FindExecutable() == null;
        }

        private async Task InstallFpsCounterAsync()
        {
            var answer = MessageBox.Show(
                "Для счётчика FPS нужен Intel PresentMon — бесплатная программа Intel с открытым исходным кодом.\n\n" +
                "• Она читает события Windows о показе кадров и в игры не внедряется: античиты такой способ не трогают.\n" +
                "• Ставится через winget из официального пакета, перед каждым запуском Ven4Tools проверяет подпись Intel.\n" +
                "• Работает только пока включён оверлей.\n\n" +
                "Установить PresentMon?",
                "Счётчик FPS", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;

            IsInstallingFpsCounter = true;
            try
            {
                var result = await PresentMonService.InstallAsync();
                AppLogger.Write($"🎮 Счётчик FPS: {result.Message}");
                RefreshOverlayState();
                // Оверлей уже показан без счётчика — перезапустить, чтобы появился FPS.
                if (result.Ok && OverlayService.Instance.IsVisible)
                {
                    OverlayService.Instance.Hide();
                    OverlayService.Instance.Show();
                }
                MessageBox.Show(result.Message, "Счётчик FPS", MessageBoxButton.OK,
                    result.Ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "DiagnosticsViewModel.InstallFpsCounter");
                MessageBox.Show("Не удалось установить счётчик FPS: " + ex.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally { IsInstallingFpsCounter = false; }
        }

        /// <summary>Вкладка показана: сразу прочитать датчики и дальше раз в 2 секунды.</summary>
        public void StartSensors()
        {
            RefreshSensorDriverState();
            if (_sensorTimer == null)
            {
                _sensorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                _sensorTimer.Tick += async (_, _) => await ReadSensorsAsync();
            }
            _sensorTimer.Start();
            _ = AcquireAndReadAsync();
        }

        private async Task AcquireAndReadAsync()
        {
            await HardwareSensorService.Instance.AcquireAsync("diagnostics");
            RefreshSensorDriverState();
            await ReadSensorsAsync();
        }

        /// <summary>Вкладка скрыта: опрос останавливается.</summary>
        public void StopSensors()
        {
            _sensorTimer?.Stop();
            _ = HardwareSensorService.Instance.ReleaseAsync("diagnostics");
        }

        private async Task ReadSensorsAsync()
        {
            // Первое открытие с драйвером идёт несколько секунд — тики, пришедшие за
            // это время, просто пропускаются, а не копятся в очередь.
            if (_sensorReadInFlight) return;
            _sensorReadInFlight = true;
            try
            {
                var cards = await HardwareSensorService.Instance.ReadAsync();
                _lastSensorCards = cards;
                SensorCards = cards;
                SensorStatusText = cards.Count == 0
                    ? "Датчики не найдены"
                    : "Обновляется раз в 2 секунды, пока открыт этот раздел";
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "DiagnosticsViewModel.ReadSensors");
                SensorStatusText = "Не удалось прочитать датчики — подробности в логе";
                _sensorTimer?.Stop();
            }
            finally { _sensorReadInFlight = false; }
        }

        private void RefreshSensorDriverState()
        {
            bool installed = PawnIoDriver.IsInstalled();
            ShowInstallSensorDriver = PawnIoDriver.FeatureEnabled && !installed;
            ShowRemoveSensorDriver = PawnIoDriver.FeatureEnabled && installed;
            if (!PawnIoDriver.FeatureEnabled)
            {
                // Экспериментальный модуль: драйвер Ven4Tools не ставит и не трогает.
                SensorDriverText = installed && PawnIoDriver.IsRunning()
                    ? "Найден драйвер датчиков другой программы (PawnIO, например от FanControl): температуры и частоты процессора видны через него. Ven4Tools этот драйвер не ставит и не выключает."
                    : "В экспериментальной версии температуры и частоты процессора не показываются: для них нужен драйвер ядра, а его мы пока не ставим, чтобы не мешать играм с античитом. Видеокарта, диски, загрузка процессора, FPS и оверлей работают.";
                return;
            }
            if (!installed)
            {
                SensorDriverText = "Температуры и частоты процессора и датчики платы Windows без драйвера не отдаёт. Драйвер необязателен: без него работают оверлей, FPS, видеокарта и диски. Ставится только по кнопке и удаляется так же.";
                return;
            }
            var others = AntiCheatDetector.OtherPawnIoUsers();
            SensorDriverText = ProfileService.Current.SensorDriverAlwaysOn
                ? "Драйвер датчиков (PawnIO) установлен и включён всегда — так попросили в настройке ниже."
                : PawnIoDriver.IsDeviceEnabled()
                    ? (others.Count > 0
                        ? $"Драйвер датчиков (PawnIO) включён: им пользуется {string.Join(", ", others)}."
                        : "Драйвер датчиков (PawnIO) включён, пока открыты датчики, и выключится, когда вы уйдёте из раздела и выключите оверлей.")
                    : "Драйвер датчиков (PawnIO) установлен и выключен. Он включается только пока открыты датчики или оверлей и после перезагрузки Windows не загружается.";
        }

        /// <summary>Показывать оверлей в играх с античитом (по умолчанию — нет).</summary>
        public bool OverlayInAntiCheatGames
        {
            get => ProfileService.Current.OverlayInAntiCheatGames;
            set
            {
                if (ProfileService.Current.OverlayInAntiCheatGames == value) return;
                if (value && MessageBox.Show(
                        "Оверлей не внедряется в игру, но античиты видят окна поверх игры. Случаев блокировки за такие окна не известно, но и гарантий никто не даёт.\n\nПоказывать оверлей в играх с античитом на свой риск?",
                        "Оверлей и античиты", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                {
                    OnPropertyChanged();
                    return;
                }
                ProfileService.Current.OverlayInAntiCheatGames = value;
                ProfileService.Save();
                OnPropertyChanged();
            }
        }

        /// <summary>Держать драйвер датчиков включённым всегда (по умолчанию — нет).</summary>
        public bool SensorDriverAlwaysOn
        {
            get => ProfileService.Current.SensorDriverAlwaysOn;
            set
            {
                if (ProfileService.Current.SensorDriverAlwaysOn == value) return;
                if (value && MessageBox.Show(
                        "Драйвер будет загружаться при каждом запуске Windows. Это нужно, если им пользуются другие программы (например, FanControl).\n\nАнтичиты, которые проверяют драйверы с момента загрузки Windows, будут его видеть. Если игра откажется запускаться — снимите эту галочку и перезагрузите компьютер.\n\nДержать драйвер включённым всегда?",
                        "Драйвер датчиков", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                {
                    OnPropertyChanged();
                    return;
                }
                ProfileService.Current.SensorDriverAlwaysOn = value;
                ProfileService.Save();
                OnPropertyChanged();
                _ = value
                    ? PawnIoDriver.EnableDeviceAsync(KitCommandRunner.Default)
                    : HardwareSensorService.Instance.ReleaseDriverAsync();
                RefreshSensorDriverState();
            }
        }

        private async Task InstallSensorDriverAsync()
        {
            // PawnIO 2.2.0 исправлял синий экран на старых Windows 10 (до версии 2004):
            // на них драйвер не предлагаем вовсе.
            if (Environment.OSVersion.Version.Build < PawnIoDriver.MinWindowsBuild)
            {
                MessageBox.Show(
                    "На этой версии Windows драйвер датчиков не ставится: на сборках Windows 10 старше 2004 он вызывал синий экран. " +
                    "Без драйвера работают оверлей, FPS, видеокарта и диски.",
                    "Драйвер датчиков", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var games = AntiCheatDetector.Installed();
            // Окно собирается из кусков — каждый переводится сам, а потом подставляется в шаблон:
            // склеенный русский текст переводчик целиком не узнал бы.
            string gamesText = games.Count == 0
                ? Tr("Античитов игр на этом компьютере не найдено.")
                : string.Format(Tr("На этом компьютере найдены античиты:\n{0}"),
                      string.Join("\n", games.Select(g => "  • " + Tr(AntiCheatDetector.Name(g))))) + "\n\n" +
                  (games.Contains(AntiCheat.Faceit)
                      ? Tr("⚠ FACEIT отказывался запускаться, пока драйвер PawnIO установлен (не загружен — именно установлен). Автор драйвера считает это исправленным, но подтверждений нет. Если вы играете через FACEIT — драйвер лучше не ставить.")
                      : Tr("Подтверждённых конфликтов с этими античитами не известно, но и гарантий никто не даёт."));

            var answer = MessageBox.Show(
                string.Format(Tr("Для датчиков процессора нужен драйвер PawnIO. Он необязателен: без него работают оверлей, FPS, видеокарта и диски — пропадут только температуры и частоты процессора и вентиляторы платы.\n\n" +
                    "• Драйвер ядра стороннего автора (namazso) с открытым исходным кодом, подписанный Microsoft. Его же используют FanControl, LibreHardwareMonitor и OpenRGB.\n" +
                    "• Ставится через winget из официального пакета (если GitHub недоступен — та же версия с сайта Ven4Tools), подпись проверяется.\n" +
                    "• Включается только пока открыты датчики или оверлей и после перезагрузки Windows не загружается — античиты, которые проверяют драйверы при загрузке, его не увидят.\n" +
                    "• Когда запускается игра с античитом, Ven4Tools сам выключает драйвер и скрывает оверлей.\n" +
                    "• Удаляется полностью одной кнопкой.\n\n{0}\n\nУстановить драйвер?"), gamesText),
                Tr("Драйвер датчиков"), MessageBoxButton.YesNo,
                games.Contains(AntiCheat.Faceit) ? MessageBoxImage.Warning : MessageBoxImage.Question,
                games.Contains(AntiCheat.Faceit) ? MessageBoxResult.No : MessageBoxResult.Yes);
            if (answer != MessageBoxResult.Yes) return;

            IsChangingSensorDriver = true;
            SensorStatusText = "Установка драйвера датчиков…";
            try
            {
                var result = await PawnIoDriver.InstallAsync();
                AppLogger.Write($"🌡 Драйвер датчиков: {result.Message}");
                await HardwareSensorService.Instance.ResetAsync();
                RefreshSensorDriverState();
                await ReadSensorsAsync();
                MessageBox.Show(result.Message, "Драйвер датчиков", MessageBoxButton.OK,
                    result.Ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "DiagnosticsViewModel.InstallSensorDriver");
                MessageBox.Show("Не удалось установить драйвер: " + ex.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally { IsChangingSensorDriver = false; }
        }

        private async Task RemoveSensorDriverAsync()
        {
            var answer = MessageBox.Show(
                "Удалить драйвер датчиков PawnIO?\n\n" +
                "Температуры и частоты процессора и датчики платы станут недоступны. " +
                "Если драйвер ставила другая программа (например, FanControl), она тоже перестанет видеть датчики.",
                "Драйвер датчиков", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;

            IsChangingSensorDriver = true;
            SensorStatusText = "Удаление драйвера датчиков…";
            try
            {
                // Библиотека держит драйвер открытым — сначала отпустить его.
                await HardwareSensorService.Instance.ResetAsync();
                var result = await PawnIoDriver.RemoveAsync(KitCommandRunner.Default);
                AppLogger.Write($"🌡 Драйвер датчиков: {result.Message}");
                RefreshSensorDriverState();
                await ReadSensorsAsync();
                MessageBox.Show(result.Message, "Драйвер датчиков", MessageBoxButton.OK,
                    result.Ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "DiagnosticsViewModel.RemoveSensorDriver");
                MessageBox.Show("Не удалось удалить драйвер: " + ex.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally { IsChangingSensorDriver = false; }
        }

        /// <summary>Раздел «Датчики» для полного отчёта — последний снимок.</summary>
        private string SensorsReportText() =>
            _lastSensorCards.Count == 0
                ? "Датчики не прочитаны (раздел «Диагностика» ещё не открывался)."
                : HardwareSnapshotBuilder.ToReport(_lastSensorCards);
    }
}
