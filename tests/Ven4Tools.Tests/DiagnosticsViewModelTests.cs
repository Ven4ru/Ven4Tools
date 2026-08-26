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
            Assert.Equal("Текущее состояние: определяется...", vm.TurboBoostStatusText);
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
            Assert.True(vm.DisableTurboBoostCommand.CanExecute(null));
            Assert.True(vm.EnableTurboBoostCommand.CanExecute(null));
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

            var brush = DiagnosticsViewModel.ResolveBrush("TextSecondary");

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
