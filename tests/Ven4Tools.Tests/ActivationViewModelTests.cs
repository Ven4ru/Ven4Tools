using Ven4Tools.ViewModels;

namespace Ven4Tools.Tests;

/// <summary>
/// Логика вкладки «Активация», перенесённая из code-behind в ViewModel
/// (2026-08-25). Реальные WMI-запросы/Process.Start (CheckActivationStatusAsync,
/// ActivateWindows/OfficeCommand) здесь не проверяются — только конструирование,
/// биндинг-состояние и построение WMI-запроса как строки.
/// </summary>
public class ActivationViewModelTests
{
    [Fact]
    public void ConsentGiven_ПоУмолчанию_False()
    {
        var vm = new ActivationViewModel();

        Assert.False(vm.ConsentGiven);
    }

    [Fact]
    public void ConsentGiven_МожноУстановитьВTrue()
    {
        var vm = new ActivationViewModel();

        vm.ConsentGiven = true;

        Assert.True(vm.ConsentGiven);
    }

    [Fact]
    public void ConsentGiven_ПоднимаетPropertyChanged()
    {
        var vm = new ActivationViewModel();
        var raised = new System.Collections.Generic.List<string>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        vm.ConsentGiven = true;

        Assert.Contains(nameof(vm.ConsentGiven), raised);
    }

    [Fact]
    public void WindowsStatusText_ИOfficeStatusText_ПоУмолчанию_Проверка()
    {
        var vm = new ActivationViewModel();

        Assert.Equal("Проверка...", vm.WindowsStatusText);
        Assert.Equal("Проверка...", vm.OfficeStatusText);
    }

    [Fact]
    public void IsCheckingStatus_ПоУмолчанию_False_КомандаДоступна()
    {
        var vm = new ActivationViewModel();

        Assert.False(vm.IsCheckingStatus);
        Assert.True(vm.CheckStatusCommand.CanExecute(null));
    }

    [Fact]
    public void CreateLicensingSearcher_СтроитЗапросПоSoftwareLicensingProduct()
    {
        var searcher = ActivationViewModel.CreateLicensingSearcher();

        Assert.Contains("SoftwareLicensingProduct", searcher.Query.QueryString);
        Assert.Contains("LicenseStatus", searcher.Query.QueryString);
        Assert.Contains("PartialProductKey IS NOT NULL", searcher.Query.QueryString);
    }

    [Fact]
    public void ActivateWindowsCommand_И_ActivateOfficeCommand_ДоступныПоУмолчанию()
    {
        var vm = new ActivationViewModel();

        Assert.True(vm.ActivateWindowsCommand.CanExecute(null));
        Assert.True(vm.ActivateOfficeCommand.CanExecute(null));
    }

    /// <summary>
    /// Начальная кисть статусов берётся из темы (ресурс TextPrimary), а когда
    /// WPF-<see cref="System.Windows.Application"/> не поднят — из фолбэка
    /// <see cref="System.Windows.Media.Brushes.White"/>. Юнит-тестовая среда —
    /// ровно этот случай: <c>Application.Current == null</c>, ни один тест
    /// проекта не создаёт Application. Тест закрепляет именно ветку фолбэка:
    /// без него замена хардкода #FFFFFFFF на ResolveDefaultStatusBrush()
    /// не покрыта ничем.
    /// </summary>
    [Fact]
    public void СтатусныеКисти_БезApplication_ПадаютВБелыйФолбэк()
    {
        Assert.Null(System.Windows.Application.Current);

        var vm = new ActivationViewModel();

        Assert.Same(System.Windows.Media.Brushes.White, vm.WindowsStatusBrush);
        Assert.Same(System.Windows.Media.Brushes.White, vm.OfficeStatusBrush);
    }
}
