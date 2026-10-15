using System;
using System.Collections.Generic;
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
            _ = ReadSensorsAsync();
        }

        /// <summary>Вкладка скрыта: опрос останавливается.</summary>
        public void StopSensors() => _sensorTimer?.Stop();

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
            ShowInstallSensorDriver = !installed;
            ShowRemoveSensorDriver = installed;
            SensorDriverText = installed
                ? (PawnIoDriver.IsRunning()
                    ? "Драйвер датчиков (PawnIO) установлен: видны температуры и частоты процессора и вентиляторы платы."
                    : "Драйвер датчиков (PawnIO) установлен, но не запущен — перезагрузите компьютер.")
                : "Температуры и частоты процессора и датчики платы Windows без драйвера не отдаёт. Драйвер ставится только по кнопке и удаляется так же.";
        }

        private async Task InstallSensorDriverAsync()
        {
            var answer = MessageBox.Show(
                "Для датчиков процессора нужен драйвер PawnIO.\n\n" +
                "• Это драйвер ядра стороннего автора (namazso) с открытым исходным кодом, подписанный Microsoft. " +
                "Его же используют LibreHardwareMonitor, FanControl и OpenRGB.\n" +
                "• Он ставится через winget из официального пакета, после установки Ven4Tools проверит подпись.\n" +
                "• Удалить его можно в любой момент кнопкой «Удалить драйвер» — полностью, вместе с пакетом в хранилище драйверов.\n\n" +
                "Некоторые онлайн-игры с античитом могут отказываться запускаться, пока в системе есть драйверы ядра для датчиков. " +
                "Если это случится — удалите драйвер.\n\n" +
                "Установить драйвер?",
                "Драйвер датчиков", MessageBoxButton.YesNo, MessageBoxImage.Question);
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
