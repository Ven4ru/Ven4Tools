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
        public void IsAllFilterSelected_УстановкаВFalse_ВызываетApplyFilter()
        {
            // SetFilterFlag пересчитывает фильтр на ЛЮБОЕ реальное изменение, включая
            // переход в false: при TwoWay-биндинге радиокнопок сосед получает false
            // ВТОРЫМ, и именно этот вызов задаёт итоговый список.
            // ApplyFilter() всегда переустанавливает DisplayedApps новым списком
            // (SetField сравнивает ссылки → PropertyChanged гарантирован даже на пустом
            // _allApps) и безусловно поднимает SelectAllState из RecomputeSelectAllState —
            // по наличию этих двух событий и ловим факт, что ApplyFilter звали.
            var vm = new InstalledViewModel();
            var raised = new List<string?>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            vm.IsAllFilterSelected = false;

            Assert.False(vm.IsAllFilterSelected);
            Assert.Contains(nameof(vm.IsAllFilterSelected), raised);
            Assert.Contains(nameof(vm.DisplayedApps), raised);
            Assert.Contains(nameof(vm.SelectAllState), raised);
        }

        [Fact]
        public void IsUnknownFilterSelected_УстановкаВTrue_ВызываетApplyFilter()
        {
            // Вторая половина семантики SetFilterFlag: переход в true фильтр пересчитывает.
            var vm = new InstalledViewModel();
            var raised = new List<string?>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            vm.IsUnknownFilterSelected = true;

            Assert.Contains(nameof(vm.IsUnknownFilterSelected), raised);
            Assert.Contains(nameof(vm.DisplayedApps), raised);
            Assert.Contains(nameof(vm.SelectAllState), raised);
        }

        [Fact]
        public void ПереключениеФильтра_НеизвестныеЗатемВсе_СбрасываетФильтрКорректно()
        {
            // Регрессия: при TwoWay-биндинге радиокнопок сеттер НОВОЙ выбранной кнопки
            // получает true ПЕРВЫМ, соседа сбрасывают в false ВТОРЫМ. Воспроизводим
            // ровно этот порядок записей для клика «Неизвестные» → «Все».
            // _allApps приватное, поэтому наблюдаем не содержимое списка, а СОСТОЯНИЕ
            // флагов на момент каждого пересчёта: последний ApplyFilter (тот, чей
            // результат и видит пользователь) обязан отработать при IsAll=true и
            // IsUnknown=false. До фикса последним был пересчёт по IsUnknown=true.
            var vm = new InstalledViewModel();
            var снимки = new List<(bool All, bool Unknown)>();
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(vm.DisplayedApps))
                    снимки.Add((vm.IsAllFilterSelected, vm.IsUnknownFilterSelected));
            };

            // Клик «Неизвестные»
            vm.IsUnknownFilterSelected = true;
            vm.IsAllFilterSelected     = false;
            // Клик «Все»
            vm.IsAllFilterSelected     = true;
            vm.IsUnknownFilterSelected = false;

            Assert.NotEmpty(снимки);
            Assert.Equal((true, false), снимки[^1]);
        }

        [Fact]
        public void OnlyUpdates_УстановкаВFalse_ВызываетApplyFilter()
        {
            // SetFieldTriggering — иная семантика, чем у SetFilterFlag: ApplyFilter
            // срабатывает на ЛЮБОЕ реальное изменение, включая переход в false.
            var vm = new InstalledViewModel();
            vm.OnlyUpdates = true; // первое включение уже вызвало ApplyFilter один раз
            var raised = new List<string?>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            vm.OnlyUpdates = false;

            Assert.Contains(nameof(vm.OnlyUpdates), raised);
            Assert.Contains(nameof(vm.DisplayedApps), raised);
            Assert.Contains(nameof(vm.SelectAllState), raised);
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
        public void RowSelectionChangedCommand_ЧастичныйВыбор_SelectAllStateNull()
        {
            // Третье состояние чекбокса «выбрать всё»: выбрана часть подходящих строк.
            var vm = new InstalledViewModel();
            var selected   = new InstalledApp { Name = "A", Available = "2.0", IsSelected = true };
            var unselected = new InstalledApp { Name = "B", Available = "2.0", IsSelected = false };
            vm.DisplayedApps = new List<InstalledApp> { selected, unselected };

            vm.RowSelectionChangedCommand.Execute(null);

            Assert.Null(vm.SelectAllState);
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
        public void DescribeWingetExitCode_КодCOM_УспехСПерезагрузкой()
        {
            // Вторая ветка «успех с требованием перезагрузки» — COM-код winget.
            var (success, reboot, reason) = InstalledViewModel.DescribeWingetExitCode(unchecked((int)0x8A15002C));

            Assert.True(success);
            Assert.True(reboot);
            Assert.Equal("", reason);
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
