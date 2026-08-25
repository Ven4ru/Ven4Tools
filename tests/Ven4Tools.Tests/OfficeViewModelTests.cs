using Ven4Tools.ViewModels;
using Xunit;

namespace Ven4Tools.Tests
{
    public class OfficeViewModelTests
    {
        [Fact]
        public void ResolveVersion_O2024_ВозвращаетOffice2024()
        {
            var (name, id) = OfficeViewModel.ResolveVersion(o2024: true, o2021: false, o2019: false, o2016: false);

            Assert.Equal("Office 2024 ProPlus", name);
            Assert.Equal("ProPlus2024Retail", id);
        }

        [Fact]
        public void ResolveVersion_O2021_ВозвращаетOffice2021()
        {
            var (name, id) = OfficeViewModel.ResolveVersion(false, true, false, false);

            Assert.Equal("Office 2021 Professional", name);
            Assert.Equal("Professional2021Retail", id);
        }

        [Fact]
        public void ResolveVersion_O2019_ВозвращаетOffice2019()
        {
            var (name, id) = OfficeViewModel.ResolveVersion(false, false, true, false);

            Assert.Equal("Office 2019 Professional", name);
            Assert.Equal("Professional2019Retail", id);
        }

        [Fact]
        public void ResolveVersion_O2016_ВозвращаетOffice2016()
        {
            var (name, id) = OfficeViewModel.ResolveVersion(false, false, false, true);

            Assert.Equal("Office 2016 Professional", name);
            Assert.Equal("ProPlusRetail", id);
        }

        [Fact]
        public void ResolveVersion_НичегоНеВыбрано_ВозвращаетOffice365Fallback()
        {
            var (name, id) = OfficeViewModel.ResolveVersion(false, false, false, false);

            Assert.Equal("Office 365 ProPlus", name);
            Assert.Equal("O365ProPlusRetail", id);
        }

        [Fact]
        public void ResolveVersion_ПриоритетO2024НадОстальными()
        {
            var (name, _) = OfficeViewModel.ResolveVersion(true, true, true, true);

            Assert.Equal("Office 2024 ProPlus", name);
        }

        [Fact]
        public void Конструктор_УстанавливаетДефолтныеЗначения()
        {
            var vm = new OfficeViewModel();

            Assert.True(vm.IsO365Selected);
            Assert.False(vm.IsO2024Selected);
            Assert.False(vm.IsO2021Selected);
            Assert.False(vm.IsO2019Selected);
            Assert.False(vm.IsO2016Selected);
            Assert.Equal("ru-ru", vm.SelectedLanguage);
            Assert.False(vm.SaveInstaller);
            Assert.False(vm.HasDownloadedInstaller);
            Assert.False(vm.IsDownloading);
            Assert.False(vm.IsInstalling);
            Assert.False(vm.CancelEnabled);
            Assert.True(vm.CancelVisible);
            Assert.False(vm.ProgressVisible);
            Assert.Equal("⏳ Подготовка...", vm.InstallPhaseText);
            Assert.Equal(0, vm.ProgressValue);
            Assert.Equal("", vm.InstallDetailText);
            Assert.False(vm.ProgressIndeterminate);
            Assert.True(vm.ActivationHintVisible);
        }

        [Fact]
        public void ВыборДругойВерсии_ОбновляетСвойство()
        {
            var vm = new OfficeViewModel();

            vm.IsO2021Selected = true;

            Assert.True(vm.IsO2021Selected);
        }

        [Fact]
        public void DownloadCommand_CanExecute_ИзначальноTrue()
        {
            var vm = new OfficeViewModel();

            Assert.True(vm.DownloadCommand.CanExecute(null));
        }

        [Fact]
        public void InstallCommand_CanExecute_ИзначальноFalse()
        {
            var vm = new OfficeViewModel();

            Assert.False(vm.InstallCommand.CanExecute(null));
        }

        [Fact]
        public void CancelCommand_CanExecute_ИзначальноFalse()
        {
            // Прямо требуется существующим UI-тестом OfficeTab_ОтменаИПереходКАктивации
            // (Ven4Tools.ClientUITests/Phase3RemainingTabsTests.cs) — кнопка «Отмена»
            // обязана быть задизейблена вне активной операции.
            var vm = new OfficeViewModel();

            Assert.False(vm.CancelCommand.CanExecute(null));
        }

        [Fact]
        public void InstallCommand_CanExecute_TrueПослеHasDownloadedInstaller()
        {
            var vm = new OfficeViewModel();

            vm.HasDownloadedInstaller = true;

            Assert.True(vm.InstallCommand.CanExecute(null));
        }

        [Fact]
        public void GoActivationCommand_ПоднимаетСобытие()
        {
            var vm = new OfficeViewModel();
            bool raised = false;
            vm.GoToActivation += () => raised = true;

            vm.GoActivationCommand.Execute(null);

            Assert.True(raised);
        }

        [Fact]
        public void OfficeLanguages_СодержитВосемьЯзыковНачинаясRuRu()
        {
            var vm = new OfficeViewModel();

            Assert.Equal(8, vm.OfficeLanguages.Length);
            Assert.Equal("ru-ru", vm.OfficeLanguages[0]);
        }
    }
}
