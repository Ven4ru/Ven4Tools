using Ven4Tools.Services.WindowsUpdate;
using Ven4Tools.Tests.Fakes;
using Ven4Tools.ViewModels;

namespace Ven4Tools.Tests;

/// <summary>
/// Скрытие обновлений: какие патчи требуют предупреждения и как скрытые/возвращённые
/// переходят между списками. Реальный Windows Update не трогается — фейковый источник.
/// </summary>
[Collection("ProfileService")]
public sealed class WindowsUpdateHideTests
{
    private static WindowsUpdateItem Item(string id, string title = "", string severity = "", params string[] categories) =>
        new()
        {
            UpdateId = id,
            Title = string.IsNullOrEmpty(title) ? $"Патч {id}" : title,
            Severity = severity,
            CategoryNames = categories
        };

    [Fact]
    public void Важный_ПоSeverity()
    {
        Assert.True(WindowsUpdateHideAdvisor.IsImportant(Item("1", severity: "Critical")));
    }

    [Theory]
    [InlineData("Security Updates")]
    [InlineData("Critical Updates")]
    [InlineData("Обновления безопасности")]
    [InlineData("Критические обновления")]
    public void Важный_ПоКатегории(string category)
    {
        Assert.True(WindowsUpdateHideAdvisor.IsImportant(Item("1", categories: category)));
    }

    [Theory]
    [InlineData("2026-10 Cumulative Update for Windows 11 Version 25H2 (KB5099999)")]
    [InlineData("2026-10 Накопительное обновление для Windows 11 (KB5099999)")]
    [InlineData("Security Intelligence Update for Microsoft Defender Antivirus - KB2267602")]
    public void Важный_ПоНазванию(string title)
    {
        Assert.True(WindowsUpdateHideAdvisor.IsImportant(Item("1", title: title)));
    }

    [Fact]
    public void Драйвер_НеВажный_НоОпознаётсяКакДрайвер()
    {
        var driver = Item("1", title: "NVIDIA - Display - 31.0.15.5222", categories: "Drivers");
        Assert.False(WindowsUpdateHideAdvisor.IsImportant(driver));
        Assert.True(WindowsUpdateHideAdvisor.IsDriver(driver));
        Assert.True(WindowsUpdateHideAdvisor.IsDriver(Item("2", categories: "Драйверы")));
    }

    [Fact]
    public void Подтверждение_ДляДрайвера_БезПредупреждения()
    {
        var (text, isWarning) = WindowsUpdateViewModel.BuildHideConfirmation(
            new[] { Item("1", title: "NVIDIA - Display", categories: "Drivers") });

        Assert.False(isWarning);
        Assert.Contains("старый драйвер", text);
        Assert.DoesNotContain("⚠️", text);
    }

    [Fact]
    public void Подтверждение_ДляНакопительного_СПредупреждениемИНазванием()
    {
        var (text, isWarning) = WindowsUpdateViewModel.BuildHideConfirmation(
            new[] { Item("1", title: "2026-10 Cumulative Update (KB5099999)") });

        Assert.True(isWarning);
        Assert.Contains("⚠️", text);
        Assert.Contains("2026-10 Cumulative Update (KB5099999)", text);
    }

    [Fact]
    public async Task Скрыть_УбираетИзСписка_ОстальныеОстаются()
    {
        var fake = new FakeWindowsUpdateSource();
        fake.Items.AddRange(new[] { Item("a"), Item("b") });
        var vm = new WindowsUpdateViewModel(new WindowsUpdateService(fake));
        await vm.RunSearchAsync();

        await vm.HideAsync(new[] { "a" });

        Assert.Equal(new[] { ("a", true) }, fake.SetHiddenCalls);
        var remaining = vm.Tree.SelectMany(c => c.Items).Select(i => i.Item.UpdateId).Distinct().ToList();
        Assert.Equal(new[] { "b" }, remaining);
        Assert.Contains("Скрыто обновлений: 1", vm.StatusText);
    }

    [Fact]
    public async Task Скрыть_Последнее_ПоказываетПустоеСостояние()
    {
        var fake = new FakeWindowsUpdateSource();
        fake.Items.Add(Item("a"));
        var vm = new WindowsUpdateViewModel(new WindowsUpdateService(fake));
        await vm.RunSearchAsync();

        await vm.HideAsync(new[] { "a" });

        Assert.Empty(vm.Tree);
        Assert.True(vm.ShowEmptyState);
    }

    [Fact]
    public async Task СписокСкрытых_Загружается_ИВозвратПереноситВНайденные()
    {
        var fake = new FakeWindowsUpdateSource();
        fake.HiddenItems.Add(Item("h"));
        var vm = new WindowsUpdateViewModel(new WindowsUpdateService(fake));
        await vm.RunSearchAsync();
        await vm.RefreshHiddenAsync();

        Assert.Single(vm.HiddenUpdates);
        Assert.Empty(vm.Tree);

        await vm.UnhideAsync(new[] { "h" });

        Assert.Empty(vm.HiddenUpdates);
        Assert.Contains(vm.Tree.SelectMany(c => c.Items), n => n.Item.UpdateId == "h");
        Assert.Equal(new[] { ("h", false) }, fake.SetHiddenCalls);
    }

    [Fact]
    public async Task ОтметкаВСпискеСкрытых_ВключаетКнопкуВернуть()
    {
        var fake = new FakeWindowsUpdateSource();
        fake.HiddenItems.Add(Item("h"));
        var vm = new WindowsUpdateViewModel(new WindowsUpdateService(fake));
        await vm.RefreshHiddenAsync();

        Assert.False(vm.IsUnhideEnabled);
        vm.HiddenUpdates[0].IsChecked = true;
        Assert.True(vm.IsUnhideEnabled);
    }

    [Fact]
    public async Task ОтметкаВДереве_ВключаетКнопкуСкрыть()
    {
        var fake = new FakeWindowsUpdateSource();
        fake.Items.Add(Item("a"));
        var vm = new WindowsUpdateViewModel(new WindowsUpdateService(fake));
        await vm.RunSearchAsync();

        Assert.False(vm.IsHideEnabled);
        vm.Tree[0].Items[0].IsChecked = true;
        Assert.True(vm.IsHideEnabled);
    }
}
