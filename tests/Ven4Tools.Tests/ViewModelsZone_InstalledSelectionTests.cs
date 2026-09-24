using System.Collections.Generic;
using Ven4Tools.ViewModels;
using Xunit;

namespace Ven4Tools.Tests
{
    /// <summary>
    /// Доступность групповых кнопок вкладки «Установленные» после смены
    /// отображаемого списка: ApplyFilter заменяет DisplayedApps, и флаги
    /// CanUpdateSelected/CanUninstallSelected обязаны пересчитываться по новому
    /// списку, а не оставаться от строк, которых на экране уже нет.
    /// </summary>
    public class ViewModelsZone_InstalledSelectionTests
    {
        [Fact]
        public void СменаФильтра_СбрасываетДоступностьГрупповыхКнопокПоСкрытымСтрокам()
        {
            var vm = new InstalledViewModel();
            var app = new InstalledApp { Name = "A", WingetId = "Vendor.A", Available = "2.0", IsSelected = true };
            vm.DisplayedApps = new List<InstalledApp> { app };
            vm.RowSelectionChangedCommand.Execute(null);
            Assert.True(vm.CanUpdateSelected);
            Assert.True(vm.CanUninstallSelected);

            // Полный список пуст — после пересчёта фильтра выбранная строка не видна.
            vm.SearchText = "zzz";

            Assert.Empty(vm.DisplayedApps);
            Assert.False(vm.CanUpdateSelected);
            Assert.False(vm.CanUninstallSelected);
            Assert.False(vm.UpdateSelectedCommand.CanExecute(null));
            Assert.False(vm.UninstallSelectedCommand.CanExecute(null));
        }
    }
}
