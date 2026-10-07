using Ven4Tools.Services;
using Ven4Tools.ViewModels;
using Xunit;

namespace Ven4Tools.Tests
{
    public class DiagnosticsViewModelTests
    {
        [Fact]
        public void Конструктор_УстанавливаетДефолтныеЗначения()
        {
            var vm = new DiagnosticsViewModel();

            Assert.Equal("Загрузка...", vm.OSVersionText);
            Assert.Equal("Загрузка...", vm.ProcessorText);
            Assert.Equal("Загрузка...", vm.RAMText);
            Assert.Equal("", vm.AppVersionText);
            Assert.Equal("Нажмите «Последний лог» для просмотра...", vm.LatestLogText);
            Assert.Equal("Диагностика ещё не запускалась", vm.HealthBadgeText);
            Assert.Equal("", vm.LastRunText);
            Assert.True(vm.ShowPlaceholders);
            Assert.Empty(vm.DiskRows);
            Assert.Empty(vm.WuRows);
            Assert.Empty(vm.RebootCards);
            Assert.Null(vm.RebootStatusRow);
            Assert.False(vm.ShowRebootStatusRow);
            Assert.False(vm.ShowDisableFastStartupButton);
            Assert.False(vm.WuButtonsVisible);
            Assert.Equal("Нажмите «Запустить диагностику»", vm.HardwareSummaryText);
            Assert.Equal("", vm.HardwareRawText);
            Assert.False(vm.HardwareRawVisible);
            Assert.Equal("Текущий режим: определяется...", vm.TurboBoostStatusText);
            Assert.Empty(vm.TurboBoostModeOptions);
            Assert.Null(vm.SelectedTurboBoostMode);
            // Пока режим не прочитан, применять нечего.
            Assert.False(vm.ApplyTurboBoostModeCommand.CanExecute(null));
            Assert.False(vm.IsRunningDiagnostics);
            Assert.False(vm.IsClearingWuCache);
        }

        [Fact]
        public void КомандыБезCanExecute_ИзначальноTrue()
        {
            var vm = new DiagnosticsViewModel();

            Assert.True(vm.CopySystemInfoCommand.CanExecute(null));
            Assert.True(vm.OpenLogsCommand.CanExecute(null));
            Assert.True(vm.OpenLatestLogCommand.CanExecute(null));
            Assert.True(vm.ClearLogsCommand.CanExecute(null));
            Assert.True(vm.OpenWindowsUpdateCommand.CanExecute(null));
            Assert.True(vm.CopyFullReportCommand.CanExecute(null));
            Assert.True(vm.DisableFastStartupCommand.CanExecute(null));
        }

        [Fact]
        public void БизиКоманды_CanExecute_ИзначальноTrue()
        {
            var vm = new DiagnosticsViewModel();

            Assert.True(vm.RunDiagnosticsCommand.CanExecute(null));
            Assert.True(vm.ClearWuCacheCommand.CanExecute(null));
            Assert.False(vm.IsApplyingTurboBoost);
            Assert.False(vm.IsDisablingFastStartup);
        }

        // Сбалансированная схема, сейчас «Включён», Windows сама задаёт «Агрессивный».
        private static DiagnosticsViewModel WithTurboState(int? ac = 1, int? dc = 1, int? windowsDefault = 2)
        {
            var vm = new DiagnosticsViewModel();
            vm.ApplyTurboBoostState(
                new TurboBoostState("381b4222-f694-41f0-9685-ff5bb260df2e", new[] { 0, 1, 2, 3, 4, 5, 6 }, ac, dc),
                windowsDefault);
            return vm;
        }

        // Смена режима правит схему электропитания через powercfg — быстрый двойной клик
        // запускал два процесса с правами администратора параллельно.
        [Fact]
        public void IsApplyingTurboBoost_ЗакрываетПрименениеРежима()
        {
            var vm = WithTurboState();
            Assert.True(vm.ApplyTurboBoostModeCommand.CanExecute(null));

            vm.IsApplyingTurboBoost = true;

            Assert.False(vm.ApplyTurboBoostModeCommand.CanExecute(null));
            // Соседние операции к турбобусту отношения не имеют и блокироваться не должны.
            Assert.True(vm.DisableFastStartupCommand.CanExecute(null));
            Assert.True(vm.RunDiagnosticsCommand.CanExecute(null));
        }

        [Fact]
        public void IsDisablingFastStartup_ЗакрываетТолькоСвоюКоманду()
        {
            var vm = WithTurboState();
            vm.IsDisablingFastStartup = true;

            Assert.False(vm.DisableFastStartupCommand.CanExecute(null));
            Assert.True(vm.ApplyTurboBoostModeCommand.CanExecute(null));
        }

        [Fact]
        public void ФлагиДлительныхОпераций_СнятыеВозвращаютCanExecute()
        {
            var vm = WithTurboState();
            vm.IsApplyingTurboBoost = true;
            vm.IsDisablingFastStartup = true;

            vm.IsApplyingTurboBoost = false;
            vm.IsDisablingFastStartup = false;

            Assert.True(vm.ApplyTurboBoostModeCommand.CanExecute(null));
            Assert.True(vm.DisableFastStartupCommand.CanExecute(null));
        }

        [Fact]
        public void РежимыТурбобуста_ТекущийИЗаводскойПомеченыВСписке()
        {
            var vm = WithTurboState(ac: 1, dc: 1, windowsDefault: 2);

            Assert.Equal(7, vm.TurboBoostModeOptions.Count);
            Assert.Equal("Отключён", vm.TurboBoostModeOptions[0].Label);
            Assert.Equal("Включён — текущий", vm.TurboBoostModeOptions[1].Label);
            Assert.Equal("Агрессивный — по умолчанию в Windows", vm.TurboBoostModeOptions[2].Label);
            // В списке сразу выбран текущий режим, под ним — его описание.
            Assert.Equal(1, vm.SelectedTurboBoostMode?.Mode);
            Assert.Equal(TurboBoostModes.Description(1), vm.SelectedTurboBoostDescription);
            Assert.Equal("Текущий режим: Включён", vm.TurboBoostStatusText);
        }

        [Fact]
        public void РежимыТурбобуста_ТекущийСовпадаетСЗаводским_ОбеПометки()
        {
            var vm = WithTurboState(ac: 2, dc: 2, windowsDefault: 2);

            Assert.Equal("Агрессивный — текущий, по умолчанию в Windows", vm.TurboBoostModeOptions[2].Label);
        }

        [Fact]
        public void РежимыТурбобуста_ОтБатареиДругойРежим_СказаноВСтроке()
        {
            var vm = WithTurboState(ac: 2, dc: 0);

            Assert.Equal("Текущий режим: Агрессивный (от батареи — Отключён)", vm.TurboBoostStatusText);
        }

        [Fact]
        public void РежимыТурбобуста_РежимНеПрочитан_СписокЕстьНоНичегоНеВыбрано()
        {
            var vm = new DiagnosticsViewModel();
            vm.ApplyTurboBoostState(TurboBoostModes.Parse(null), windowsDefault: null);

            Assert.Equal("Текущий режим: не удалось определить", vm.TurboBoostStatusText);
            Assert.Equal(new[] { 0, 1, 2, 3, 4 }, vm.TurboBoostModeOptions.Select(o => o.Mode));
            Assert.Null(vm.SelectedTurboBoostMode);
            Assert.False(vm.ApplyTurboBoostModeCommand.CanExecute(null));
            Assert.Equal("", vm.SelectedTurboBoostDescription);
        }

        [Fact]
        public void РежимыТурбобуста_ВыборДругогоРежима_МеняетОписание()
        {
            var vm = WithTurboState();

            vm.SelectedTurboBoostMode = vm.TurboBoostModeOptions[0];

            Assert.Equal(TurboBoostModes.Description(0), vm.SelectedTurboBoostDescription);
        }

        [Fact]
        public void OpenWindowsUpdateCommand_ПоднимаетСобытие()
        {
            var vm = new DiagnosticsViewModel();
            bool raised = false;
            vm.GoToWindowsUpdate += () => raised = true;

            vm.OpenWindowsUpdateCommand.Execute(null);

            Assert.True(raised);
        }

        [Fact]
        public void ResolveBrush_БезApplication_ПадаетВБелыйФолбэк()
        {
            Assert.Null(System.Windows.Application.Current);

            var brush = Ven4Tools.Helpers.BrushResolver.Resolve("TextSecondary");

            Assert.Same(System.Windows.Media.Brushes.White, brush);
        }

        [Fact]
        public void HealthBadgeBrush_ДефолтноеЗначение_БелыйФолбэк()
        {
            var vm = new DiagnosticsViewModel();

            Assert.Same(System.Windows.Media.Brushes.White, vm.HealthBadgeBrush);
        }
    }
}
