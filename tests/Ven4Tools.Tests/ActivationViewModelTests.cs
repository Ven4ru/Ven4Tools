using Ven4Tools.ViewModels;

namespace Ven4Tools.Tests;

/// <summary>
/// Логика вкладки состояния лицензий. Реальные WMI-запросы и запуск OSPP.VBS
/// (CheckActivationStatusAsync) здесь не проверяются — только конструирование,
/// биндинг-состояние и построение WMI-запроса как строки.
/// </summary>
public class ActivationViewModelTests
{
    /// <summary>
    /// Вкладка только показывает состояние лицензий. Команд, которые что-то открывают
    /// или запускают ради активации, у неё быть не должно — как и любых упоминаний
    /// инструментов активации в самой сборке клиента.
    /// </summary>
    [Fact]
    public void Вкладка_ТолькоПоказываетСтатус_БезКомандАктивации()
    {
        var commands = typeof(ActivationViewModel).GetProperties()
            .Where(p => p.PropertyType == typeof(RelayCommand))
            .Select(p => p.Name)
            .ToArray();

        Assert.Equal(new[] { nameof(ActivationViewModel.CheckStatusCommand) }, commands);
        Assert.Null(typeof(ActivationViewModel).Assembly.GetType("Ven4Tools.Views.MasGuideWindow"));
    }

    /// <summary>
    /// Разметка клиента не упоминает инструменты активации и не обещает активацию
    /// среди возможностей: проверка одного только типа окна выше этого не ловит —
    /// строка может остаться в «О программе» или на соседней вкладке.
    /// </summary>
    [Fact]
    public void Разметка_БезУпоминанийИнструментовАктивации()
    {
        string client = Path.Combine(RepositoryRoot(), "Ven4Tools");
        var forbidden = new[] { "Activation Scripts", "massgrave", "(MAS)", "Активация Windows и Office", "Перейдите к активации" };

        var hits = Directory.EnumerateFiles(client, "*.xaml", SearchOption.AllDirectories)
            .Where(file => !file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                        && !file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .SelectMany(file => forbidden
                .Where(word => File.ReadAllText(file).Contains(word, StringComparison.OrdinalIgnoreCase))
                .Select(word => $"{Path.GetFileName(file)}: {word}"))
            .ToArray();

        Assert.Empty(hits);
    }

    private static string RepositoryRoot([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

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
