using System;
using System.Threading.Tasks;
using Ven4Tools.Services;

namespace Ven4Tools.ViewModels
{
    /// <summary>Блок «Батарея ноутбука»: ёмкость от заводской и циклы. Нет батареи — блок скрыт.</summary>
    public sealed partial class DiagnosticsViewModel
    {
        private bool _batteryChecked;
        private bool _batteryReadInFlight;
        private BatteryHealthResult? _lastBattery;

        private bool _showBattery;
        public bool ShowBattery { get => _showBattery; private set => SetField(ref _showBattery, value); }

        private string _batteryText = "";
        public string BatteryText { get => _batteryText; private set => SetField(ref _batteryText, value); }

        private async Task RefreshBatteryAsync()
        {
            if (_batteryReadInFlight) return;
            _batteryReadInFlight = true;
            try
            {
                var result = await BatteryHealthService.ReadAsync();
                _lastBattery = result;
                _batteryChecked = true;
                ShowBattery = result != null;
                BatteryText = result?.Text ?? "";
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "DiagnosticsViewModel.RefreshBattery");
            }
            finally { _batteryReadInFlight = false; }
        }

        private string BatteryReportLine() =>
            !_batteryChecked ? "Не проверялась: раздел «Диагностика» ещё не открывался."
            : _lastBattery == null ? "Батареи нет (настольный компьютер или батарея не определяется)."
            : _lastBattery.Text;
    }
}
