using Ven4Tools.Models;
using Ven4Tools.Services.DiskBenchmark;
using Ven4Tools.ViewModels;
using Xunit;

namespace Ven4Tools.Tests
{
    public class BenchmarkViewModelTests
    {
        [Fact]
        public void Конструктор_УстанавливаетДефолты()
        {
            var vm = new BenchmarkViewModel();

            Assert.Equal("Определение накопителей...", vm.DiskHintText);
            Assert.Equal("—", vm.ModelText);
            Assert.Equal("—", vm.CapacityText);
            Assert.Equal("—", vm.MediaText);
            Assert.Equal("—", vm.ConnectionText);
            Assert.Equal("—", vm.CeilingText);
            Assert.Empty(vm.DiskOptions);
            Assert.Empty(vm.VolumeOptions);
            Assert.Empty(vm.FileSizeOptions);
            Assert.Equal("Normal", vm.ProfileTag);
            Assert.Empty(vm.WarningTexts);
            Assert.False(vm.ShowWarnings);
            Assert.Equal("▶ Запустить тест", vm.RunButtonText);
            Assert.Equal("Тест ещё не запускался", vm.RunStatusText);
            Assert.True(vm.IsRunEnabled);
            Assert.True(vm.IsControlsEnabled);
            Assert.False(vm.ShowProgress);
            Assert.Equal(0, vm.ProgressValue);
            Assert.False(vm.IsCopyReportEnabled);
            Assert.False(vm.IsSaveReportEnabled);
        }

        [Fact]
        public void Конструктор_УстанавливаетПустыеСтрокиРезультатов()
        {
            var vm = new BenchmarkViewModel();

            Assert.Equal(BenchmarkPresets.Patterns.Length, vm.ResultRows.Count);
            for (int i = 0; i < vm.ResultRows.Count; i++)
            {
                Assert.Equal(BenchmarkPresets.Patterns[i].Name, vm.ResultRows[i].Name);
                Assert.Equal("—", vm.ResultRows[i].ReadValueText);
                Assert.Equal("", vm.ResultRows[i].ReadSubText);
                Assert.Equal("—", vm.ResultRows[i].WriteValueText);
                Assert.Equal("", vm.ResultRows[i].WriteSubText);
            }
        }

        [Fact]
        public void Конструктор_УстанавливаетПлейсхолдерВыводов()
        {
            var vm = new BenchmarkViewModel();

            Assert.Single(vm.ConclusionLines);
            Assert.Equal("Запустите тест, чтобы увидеть разбор результата", vm.ConclusionLines[0].Text);
        }

        [Fact]
        public void RunBenchmarkCommand_CanExecute_ВсегдаTrue()
        {
            var vm = new BenchmarkViewModel();
            Assert.True(vm.RunBenchmarkCommand.CanExecute(null));
        }

        [Fact]
        public void ProfileTag_Изменение_ПоднимаетPropertyChanged()
        {
            var vm = new BenchmarkViewModel();
            bool raised = false;
            vm.PropertyChanged += (_, e) => raised |= e.PropertyName == nameof(BenchmarkViewModel.ProfileTag);

            vm.ProfileTag = "Fast";

            Assert.Equal("Fast", vm.ProfileTag);
            Assert.True(raised);
        }

        [Fact]
        public void BenchmarkResultRow_AutomationId_ВычисляетсяПоIndex()
        {
            var row = new BenchmarkResultRow
            {
                Index = 2,
                Name = "RND4K Q32T16",
                ReadValueText = "—", ReadSubText = "", WriteValueText = "—", WriteSubText = ""
            };

            Assert.Equal("txtP2Name", row.NameAutomationId);
            Assert.Equal("txtP2Read", row.ReadAutomationId);
            Assert.Equal("txtP2ReadSub", row.ReadSubAutomationId);
            Assert.Equal("txtP2Write", row.WriteAutomationId);
            Assert.Equal("txtP2WriteSub", row.WriteSubAutomationId);
        }

        [Fact]
        public void ShowDiskDetails_СДиском_ЗаполняетТексты()
        {
            var vm = new BenchmarkViewModel();
            var disk = new PhysicalDiskInfo
            {
                Index = 0,
                FriendlyName = "Тестовый SSD",
                SizeBytes = 512L * 1024 * 1024 * 1024,
                Bus = DiskBusKind.Nvme,
                Media = DiskMediaKind.Ssd,
                Link = new PciLinkInfo { Generation = 4, Width = 4 }
            };

            vm.ShowDiskDetails(disk);

            Assert.Equal("Тестовый SSD", vm.ModelText);
            Assert.NotEqual("—", vm.CapacityText);
            Assert.NotEqual("—", vm.ConnectionText);
            Assert.EndsWith("МБ/с", vm.CeilingText);
        }

        [Fact]
        public void ShowDiskDetails_БезДиска_СбрасываетТекстыВПрочерк()
        {
            var vm = new BenchmarkViewModel();

            vm.ShowDiskDetails(null);

            Assert.Equal("—", vm.ModelText);
            Assert.Equal("—", vm.CapacityText);
            Assert.Equal("—", vm.MediaText);
            Assert.Equal("—", vm.ConnectionText);
            Assert.Equal("—", vm.CeilingText);
        }

        [Fact]
        public void ShowDiskDetails_НеизвестныйПотолок_ПоказываетЧестноеНеизвестно()
        {
            var vm = new BenchmarkViewModel();
            var disk = new PhysicalDiskInfo
            {
                Index = 0,
                FriendlyName = "Диск без известной линии",
                SizeBytes = 1024L * 1024 * 1024,
                Link = PciLinkInfo.Unknown
            };

            vm.ShowDiskDetails(disk);

            Assert.Contains("неизвестно", vm.CeilingText);
        }

        [Fact]
        public void ClearResults_ЗаполняетЗаглушкиИСтатусИзмерения()
        {
            var vm = new BenchmarkViewModel();

            vm.ClearResults();

            Assert.Equal(BenchmarkPresets.Patterns.Length, vm.ResultRows.Count);
            Assert.All(vm.ResultRows, row => Assert.Equal("—", row.ReadValueText));
            Assert.Single(vm.ConclusionLines);
            Assert.Equal("Идёт измерение...", vm.ConclusionLines[0].Text);
        }

        [Fact]
        public void ShowResults_СИзмерениями_ЗаполняетСтрокуИВыводы()
        {
            var vm = new BenchmarkViewModel();
            var pattern = BenchmarkPresets.Patterns[0];
            var result = new BenchmarkRunResult
            {
                Profile = BenchmarkProfile.Fast,
                Passes = 1,
                FileSizeBytes = 1024
            };
            result.Measurements.Add(new BenchmarkMeasurement
            {
                PatternName = pattern.Name,
                Operation = BenchmarkOperation.Read,
                MegabytesPerSecond = 500,
                OperationsPerSecond = 1000,
                AverageLatencyMicroseconds = 50
            });

            vm.ShowResults(result);

            Assert.Contains("МБ/с", vm.ResultRows[0].ReadValueText);
            Assert.NotEqual("—", vm.ResultRows[0].ReadValueText);
            Assert.NotEmpty(vm.ConclusionLines);
        }

        [Fact]
        public void ShowResults_БезИзмерений_ПоказываетЗамерыНеВыполнены()
        {
            var vm = new BenchmarkViewModel();
            var result = new BenchmarkRunResult { Profile = BenchmarkProfile.Fast, Passes = 1, FileSizeBytes = 1024 };

            vm.ShowResults(result);

            Assert.Single(vm.ConclusionLines);
            Assert.Equal("• Замеры не выполнены.", vm.ConclusionLines[0].Text);
        }

        [Theory]
        [InlineData("Fast", BenchmarkProfile.Fast)]
        [InlineData("Precise", BenchmarkProfile.Precise)]
        [InlineData("Normal", BenchmarkProfile.Normal)]
        [InlineData("НеизвестныйТег", BenchmarkProfile.Normal)]
        public void SelectedProfile_МапитПоТегуСДефолтомNormal(string tag, BenchmarkProfile expected)
        {
            var vm = new BenchmarkViewModel { ProfileTag = tag };

            Assert.Equal(expected, vm.SelectedProfile);
        }

        private static PhysicalDiskInfo MakeDisk(params BenchmarkVolumeInfo[] volumes)
        {
            var disk = new PhysicalDiskInfo { Index = 0, FriendlyName = "Тестовый диск", SizeBytes = 1024L * 1024 * 1024 };
            foreach (var volume in volumes) disk.Volumes.Add(volume);
            return disk;
        }

        [Fact]
        public void SelectedDiskOption_ПредпочитаетНесистемныйТом()
        {
            var vm = new BenchmarkViewModel();
            var system = new BenchmarkVolumeInfo { Letter = "C:", IsReady = true, IsSystem = true, TotalBytes = 100, FreeBytes = 50 };
            var data = new BenchmarkVolumeInfo { Letter = "D:", IsReady = true, IsSystem = false, TotalBytes = 100, FreeBytes = 50 };
            var disk = MakeDisk(system, data);

            vm.SelectedDiskOption = new DiskOptionItem { Label = "Диск 0", Disk = disk, CanBenchmark = true };

            Assert.Equal(2, vm.VolumeOptions.Count);
            Assert.NotNull(vm.SelectedVolumeOption);
            Assert.Same(data, vm.SelectedVolumeOption!.Volume);
        }

        [Fact]
        public void SelectedDiskOption_ФильтруетНеготовыеТома()
        {
            var vm = new BenchmarkViewModel();
            var notReady = new BenchmarkVolumeInfo { Letter = "E:", IsReady = false, TotalBytes = 100, FreeBytes = 50 };
            var ready = new BenchmarkVolumeInfo { Letter = "F:", IsReady = true, TotalBytes = 100, FreeBytes = 50 };
            var disk = MakeDisk(notReady, ready);

            vm.SelectedDiskOption = new DiskOptionItem { Label = "Диск 0", Disk = disk, CanBenchmark = true };

            Assert.Single(vm.VolumeOptions);
            Assert.Same(ready, vm.VolumeOptions[0].Volume);
        }

        [Fact]
        public void SelectedDiskOption_БезГотовыхТомов_БлокируетЗапуск()
        {
            var vm = new BenchmarkViewModel();
            var notReady = new BenchmarkVolumeInfo { Letter = "E:", IsReady = false, TotalBytes = 100, FreeBytes = 50 };
            var disk = MakeDisk(notReady);

            vm.SelectedDiskOption = new DiskOptionItem { Label = "Диск 0", Disk = disk, CanBenchmark = false };

            Assert.Empty(vm.VolumeOptions);
            Assert.Null(vm.SelectedVolumeOption);
            Assert.False(vm.IsRunEnabled);
            Assert.Contains("нет тома, пригодного для теста", vm.DiskHintText);
        }

        [Fact]
        public void SelectedDiskOption_Null_СбрасываетДеталиИТома()
        {
            var vm = new BenchmarkViewModel();
            var data = new BenchmarkVolumeInfo { Letter = "D:", IsReady = true, IsSystem = false, TotalBytes = 100, FreeBytes = 50 };
            vm.SelectedDiskOption = new DiskOptionItem { Label = "Диск 0", Disk = MakeDisk(data), CanBenchmark = true };

            vm.SelectedDiskOption = null;

            Assert.Equal("—", vm.ModelText);
            Assert.Empty(vm.VolumeOptions);
            Assert.Null(vm.SelectedVolumeOption);
        }
    }
}
