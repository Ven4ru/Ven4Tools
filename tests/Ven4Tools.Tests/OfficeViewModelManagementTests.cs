using Ven4Tools.Services;
using Ven4Tools.ViewModels;

namespace Ven4Tools.Tests;

public sealed class OfficeViewModelManagementTests
{
    private sealed class FakeDetector : IOfficeInstallationDetector
    {
        public InstalledOfficeInfo Result { get; set; } = InstalledOfficeInfo.None;
        public int DetectCallCount { get; private set; }
        public InstalledOfficeInfo Detect()
        {
            DetectCallCount++;
            return Result;
        }
    }

    [Fact]
    public void OfficeНеНайден_КарточкаСкрыта()
    {
        var vm = new OfficeViewModel(new FakeDetector());

        Assert.False(vm.ShowInstalledCard);
    }

    [Fact]
    public void C2RНайден_КарточкаПоказана_КнопкиУдалить_и_ЗаменитьДоступны()
    {
        var detector = new FakeDetector
        {
            Result = new InstalledOfficeInfo
            {
                Kind = OfficeInstallationKind.ClickToRun,
                DisplayName = "Office 2016 Professional",
                Platform = "x64",
                Culture = "ru-ru",
                Version = "16.0.17928.20114",
                ProductIds = new[] { "ProPlusRetail" }
            }
        };
        var vm = new OfficeViewModel(detector);

        Assert.True(vm.ShowInstalledCard);
        Assert.False(vm.IsMsiInstallation);
        Assert.Contains("Office 2016 Professional", vm.InstalledSummaryText);
        Assert.True(vm.UninstallCommand.CanExecute(null));
    }

    [Fact]
    public void MSIНайден_ЗаменитьИУдалитьНедоступны_МожноТолькоОткрытьПанель()
    {
        var detector = new FakeDetector
        {
            Result = new InstalledOfficeInfo
            {
                Kind = OfficeInstallationKind.Msi,
                DisplayName = "Microsoft Office Professional Plus 2013",
                Version = "15.0.5361.1000"
            }
        };
        var vm = new OfficeViewModel(detector);

        Assert.True(vm.ShowInstalledCard);
        Assert.True(vm.IsMsiInstallation);
        Assert.False(vm.UninstallCommand.CanExecute(null));
        Assert.False(vm.ReplaceCommand.CanExecute(null));
    }

    [Fact]
    public void ВыбраннаяВерсияСовпадаетСУстановленной_ЗаменитьЗаблокирована()
    {
        var detector = new FakeDetector
        {
            Result = new InstalledOfficeInfo
            {
                Kind = OfficeInstallationKind.ClickToRun,
                DisplayName = "Office 2016 Professional",
                ProductIds = new[] { "ProPlusRetail" }
            }
        };
        var vm = new OfficeViewModel(detector) { IsO2016Selected = true };

        Assert.True(vm.IsReplaceBlocked);
        Assert.False(vm.ReplaceCommand.CanExecute(null));
    }

    [Fact]
    public void ВыбраннаяВерсияОтличаетсяОтУстановленной_ЗаменитьДоступна()
    {
        var detector = new FakeDetector
        {
            Result = new InstalledOfficeInfo
            {
                Kind = OfficeInstallationKind.ClickToRun,
                DisplayName = "Office 2016 Professional",
                ProductIds = new[] { "ProPlusRetail" }
            }
        };
        // Дефолт конструктора — Office 365 ProPlus (IsO365Selected = true), отличается
        // от установленного ProPlusRetail (2016) — «Заменить» должна быть доступна.
        var vm = new OfficeViewModel(detector);

        Assert.False(vm.IsReplaceBlocked);
        Assert.True(vm.ReplaceCommand.CanExecute(null));
    }

    [Fact]
    public void Детекция_НеЗапускаетсяВКонструкторе_АТолькоПриПервомЧтенииКарточки()
    {
        var detector = new FakeDetector();

        var vm = new OfficeViewModel(detector);

        Assert.Equal(0, detector.DetectCallCount);

        _ = vm.ShowInstalledCard;

        Assert.Equal(1, detector.DetectCallCount);
    }
}
