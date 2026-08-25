using System.Collections.Generic;
using Ven4Tools.ViewModels;
using Xunit;

namespace Ven4Tools.Tests
{
    public class InstalledViewModelTests
    {
        [Fact]
        public void Конструктор_УстанавливаетДефолтныеЗначения()
        {
            var vm = new InstalledViewModel();

            Assert.True(vm.IsAllFilterSelected);
            Assert.False(vm.IsUnknownFilterSelected);
            Assert.False(vm.OnlyUpdates);
            Assert.Equal("", vm.SearchText);
            Assert.Equal(0, vm.SortIndex);
            Assert.True(vm.IsLoading);
            Assert.False(vm.IsEmpty);
            Assert.False(vm.IsListVisible);
            Assert.Equal("⏳ Получение списка установленных приложений...", vm.LoadingMessage);
            Assert.Empty(vm.DisplayedApps);
            Assert.Equal("", vm.StatsText);
            Assert.Equal((bool?)false, vm.SelectAllState);
            Assert.False(vm.CanUpdateSelected);
            Assert.False(vm.CanUninstallSelected);
        }

        [Fact]
        public void ОдиночныеКоманды_CanExecute_ИзначальноTrue()
        {
            var vm = new InstalledViewModel();

            Assert.True(vm.RefreshCommand.CanExecute(null));
            Assert.True(vm.UpgradeAllCommand.CanExecute(null));
            Assert.True(vm.ExportCommand.CanExecute(null));
            Assert.True(vm.ImportCommand.CanExecute(null));
        }

        [Fact]
        public void ГрупповыеКоманды_CanExecute_ИзначальноFalse()
        {
            // Прямо требуется существующим UI-поведением: btnUpdateSelected/btnUninstallSelected
            // стартуют статическим IsEnabled="False" в оригинальном XAML.
            var vm = new InstalledViewModel();

            Assert.False(vm.UpdateSelectedCommand.CanExecute(null));
            Assert.False(vm.UninstallSelectedCommand.CanExecute(null));
        }

        [Fact]
        public void SelectAllState_УстановкаВTrue_ВыбираетТолькоПодходящиеСтроки()
        {
            var vm = new InstalledViewModel();
            var withUpdate    = new InstalledApp { Name = "A", Available = "2.0" };
            var withoutUpdate = new InstalledApp { Name = "B", Available = "" };
            var processing    = new InstalledApp { Name = "C", Available = "2.0", IsProcessing = true };
            vm.DisplayedApps = new List<InstalledApp> { withUpdate, withoutUpdate, processing };

            vm.SelectAllState = true;

            Assert.True(withUpdate.IsSelected);
            Assert.False(withoutUpdate.IsSelected);
            Assert.False(processing.IsSelected); // !CanAct — не трогаем
        }

        [Fact]
        public void SelectAllState_УстановкаВFalse_СнимаетВыборСоВсех()
        {
            var vm = new InstalledViewModel();
            var app = new InstalledApp { Name = "A", Available = "2.0", IsSelected = true };
            vm.DisplayedApps = new List<InstalledApp> { app };

            vm.SelectAllState = false;

            Assert.False(app.IsSelected);
        }

        [Fact]
        public void RowSelectionChangedCommand_ПересчитываетCanUpdateSelected()
        {
            var vm = new InstalledViewModel();
            var app = new InstalledApp { Name = "A", Available = "2.0", IsSelected = true };
            vm.DisplayedApps = new List<InstalledApp> { app };

            vm.RowSelectionChangedCommand.Execute(null);

            Assert.True(vm.CanUpdateSelected);
            Assert.True(vm.CanUninstallSelected);
        }

        [Fact]
        public void DescribeWingetExitCode_КодНоль_УспехБезПерезагрузки()
        {
            var (success, reboot, reason) = InstalledViewModel.DescribeWingetExitCode(0);

            Assert.True(success);
            Assert.False(reboot);
            Assert.Equal("", reason);
        }

        [Fact]
        public void DescribeWingetExitCode_Код3010_УспехСПерезагрузкой()
        {
            var (success, reboot, _) = InstalledViewModel.DescribeWingetExitCode(3010);

            Assert.True(success);
            Assert.True(reboot);
        }

        [Fact]
        public void DescribeWingetExitCode_ПроизвольныйКод_НеУспех()
        {
            var (success, reboot, reason) = InstalledViewModel.DescribeWingetExitCode(1);

            Assert.False(success);
            Assert.False(reboot);
            Assert.NotEmpty(reason);
        }

        [Fact]
        public void InstalledApp_Дефолты()
        {
            var app = new InstalledApp();

            Assert.False(app.HasUpdate);
            Assert.True(app.CanAct);
            Assert.False(app.IsVerified);
            Assert.True(app.IsUnknownSource);
            Assert.Equal("❓ Неизвестный", app.SourceDisplay);
        }
    }
}
