using System.Threading.Tasks;
using System.Windows;
using Ven4Tools.Services;
using Ven4Tools.Services.WindowsUpdate;

namespace Ven4Tools.ViewModels
{
    /// <summary>
    /// Переключатель «Не получать драйверы через Windows Update» — политика
    /// ExcludeWUDriversInQualityUpdate. Снижает вероятность, что Windows Update вернёт
    /// старый драйвер (чаще всего видеокарты) поверх нового, но не гарантирует этого.
    /// </summary>
    public sealed partial class WindowsUpdateViewModel
    {
        private readonly WindowsUpdateDriverPolicy _driverPolicy;

        public RelayCommand ToggleDriverBlockCommand { get; private set; } = null!;

        private bool _isDriverBlockOn;
        public bool IsDriverBlockOn { get => _isDriverBlockOn; private set => SetField(ref _isDriverBlockOn, value); }

        private string _driverBlockStatusText = "";
        public string DriverBlockStatusText { get => _driverBlockStatusText; private set => SetField(ref _driverBlockStatusText, value); }

        private void InitDriverCommands()
        {
            ToggleDriverBlockCommand = RelayCommand.FromAsync(_ => ToggleDriverBlockAsync());
        }

        internal void RefreshDriverPolicyState()
        {
            bool? blocked = _driverPolicy.IsBlocked();
            IsDriverBlockOn = blocked == true;
            // Чекбокс с OneWay-привязкой мог уже переключиться щелчком — возвращаем его к
            // фактическому состоянию реестра, даже если значение свойства не поменялось.
            OnPropertyChanged(nameof(IsDriverBlockOn));
            DriverBlockStatusText = blocked switch
            {
                true => "Сейчас: включено — Windows Update не должен предлагать драйверы вместе с ежемесячными обновлениями.",
                false => "Сейчас: выключено — Windows Update может ставить драйверы сам.",
                null => "Состояние политики прочитать не удалось."
            };
        }

        private async Task ToggleDriverBlockAsync()
        {
            bool turnOn = _driverPolicy.IsBlocked() != true;
            string text = turnOn
                ? "Не получать драйверы через Windows Update?\n\n" +
                  "Будет включена политика «Не включать драйверы в обновления качества Windows». " +
                  "Это снижает вероятность, что Windows Update вернёт старый драйвер видеокарты поверх нового, " +
                  "но не гарантирует: политика работает не во всех случаях, а драйверы для нового оборудования Windows может ставить и сама.\n\n" +
                  "Свежие драйверы придётся ставить вручную — с сайта производителя.\n" +
                  "Выключить можно здесь же, прежнее значение вернётся."
                : "Снова получать драйверы через Windows Update?\n\n" +
                  "Политика будет возвращена в состояние, которое было до включения в Ven4Tools.";
            var answer = MessageBox.Show(text, "Драйверы через Windows Update", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
            {
                RefreshDriverPolicyState();
                return;
            }

            bool ok = await Task.Run(() => turnOn ? _driverPolicy.Block() : _driverPolicy.Unblock());
            AppLogger.Write(ok
                ? (turnOn
                    ? "🛡️ Windows Update: включён запрет драйверов (ExcludeWUDriversInQualityUpdate = 1)"
                    : "🛡️ Windows Update: запрет драйверов выключен, прежнее значение политики возвращено")
                : (turnOn
                    ? "🛡️ Windows Update: не удалось включить запрет драйверов"
                    : "🛡️ Windows Update: не удалось выключить запрет драйверов"));
            RefreshDriverPolicyState();
            if (!ok)
                MessageBox.Show("Не удалось изменить политику в реестре. Подробности — в логе.",
                    "Драйверы через Windows Update", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
