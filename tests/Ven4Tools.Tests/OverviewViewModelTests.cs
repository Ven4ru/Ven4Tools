using Ven4Tools.ViewModels;

namespace Ven4Tools.Tests;

/// <summary>
/// Тексты экрана «Обзор»: по числу из фоновой проверки пользователь должен понять,
/// нужно ли что-то делать, а «ещё не проверяли» не должно выглядеть как «всё в порядке».
/// </summary>
public sealed class OverviewViewModelTests
{
    [Theory]
    [InlineData(3, true, "Можно обновить: 3", true)]
    [InlineData(3, false, "Можно обновить: 3", true)]
    [InlineData(0, true, "Все программы актуальны", false)]
    [InlineData(-1, true, "Проверка ещё не выполнялась", false)]
    [InlineData(-1, false, "Фоновая проверка выключена", false)]
    public void ОбновленияПрограмм_ТекстИПризнакВнимания(int count, bool backgroundEnabled, string status, bool attention)
    {
        var result = OverviewViewModel.DescribeAppUpdates(count, backgroundEnabled);

        Assert.Equal(status, result.Status);
        Assert.Equal(attention, result.NeedsAttention);
        Assert.False(string.IsNullOrWhiteSpace(result.Hint));
    }

    [Theory]
    [InlineData(2, true, "Найдено патчей: 2", true)]
    [InlineData(0, true, "Новых патчей не найдено", false)]
    [InlineData(0, false, "Фоновая проверка выключена", false)]
    public void WindowsUpdate_ТекстИПризнакВнимания(int count, bool backgroundEnabled, string status, bool attention)
    {
        var result = OverviewViewModel.DescribeWindowsUpdate(count, backgroundEnabled);

        Assert.Equal(status, result.Status);
        Assert.Equal(attention, result.NeedsAttention);
    }

    [Fact]
    public void WindowsUpdate_РежимЕщёНеВыбран_НеНазываетПроверкуВыключенной()
    {
        // Свежая установка: раздел «Windows Update» ни разу не открывали, проверку никто
        // не выключал — карточка говорит, как она включится.
        var result = OverviewViewModel.DescribeWindowsUpdate(-1, backgroundEnabled: false, neverOpened: true);

        Assert.Equal("Фоновая проверка ещё не включена", result.Status);
        Assert.Contains("Windows Update", result.Hint);
        Assert.False(result.NeedsAttention);
    }

    [Fact]
    public void WindowsUpdate_НайденныеПатчиВажнееНевыбранногоРежима()
    {
        var result = OverviewViewModel.DescribeWindowsUpdate(3, backgroundEnabled: false, neverOpened: true);

        Assert.Equal("Найдено патчей: 3", result.Status);
        Assert.True(result.NeedsAttention);
    }

    [Theory]
    [InlineData(false, false, "Ничего не требует внимания.")]
    [InlineData(true, false, "Есть обновления программ, остальное в порядке.")]
    [InlineData(false, true, "Есть патчи Windows, остальное в порядке.")]
    [InlineData(true, true, "Есть обновления программ и патчи Windows.")]
    public void Сводка_НазываетТоЧтоТребуетВнимания(bool apps, bool windows, string expected)
    {
        Assert.Equal(expected, OverviewViewModel.DescribeSummary(apps, windows));
    }
}
