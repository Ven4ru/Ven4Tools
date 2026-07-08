using Ven4Tools.Models;
using Ven4Tools.Services;
using Ven4Tools.Services.WindowsUpdate;
using Ven4Tools.Tests.Fakes;

namespace Ven4Tools.Tests;

public sealed class WindowsUpdateBackgroundServiceTests
{
    [Fact]
    public async Task CheckOnceAsync_ModeNotSet_DoesNotSearch()
    {
        ProfileService.Current.WindowsUpdateMode = "NotSet";
        var fake = new FakeWindowsUpdateSource();
        fake.Items.Add(new WindowsUpdateItem { UpdateId = "1", Title = "A" });
        var bg = new WindowsUpdateBackgroundService(new WindowsUpdateService(fake));

        await bg.CheckOnceAsync(CancellationToken.None);

        // Поиск не должен был случиться — при NotSet просто нечего проверять.
        // (Косвенная проверка: счётчик не должен был обновиться до 1.)
        Assert.NotEqual(1, WindowsUpdateBackgroundService.AvailableCount);
        // Прямая проверка: SearchAsync не вызывался вовсе.
        Assert.Equal(0, fake.SearchCallCount);
    }

    [Fact]
    public async Task CheckOnceAsync_ModeNotifyOnly_UpdatesCountFromSearch()
    {
        ProfileService.Current.WindowsUpdateMode = "NotifyOnly";
        ProfileService.Current.ParanoidMode = false;
        ProfileService.Current.OfflineMode = false;
        var fake = new FakeWindowsUpdateSource();
        fake.Items.Add(new WindowsUpdateItem { UpdateId = "1", Title = "A" });
        fake.Items.Add(new WindowsUpdateItem { UpdateId = "2", Title = "B" });
        var bg = new WindowsUpdateBackgroundService(new WindowsUpdateService(fake));

        await bg.CheckOnceAsync(CancellationToken.None);

        Assert.Equal(2, WindowsUpdateBackgroundService.AvailableCount);
    }

    [Fact]
    public async Task CheckOnceAsync_ParanoidMode_SkipsCheck()
    {
        ProfileService.Current.WindowsUpdateMode = "NotifyOnly";
        ProfileService.Current.ParanoidMode = true;
        var fake = new FakeWindowsUpdateSource();
        fake.Items.Add(new WindowsUpdateItem { UpdateId = "1", Title = "A" });
        var bg = new WindowsUpdateBackgroundService(new WindowsUpdateService(fake));
        WindowsUpdateBackgroundService.CountChangedResetForTests();

        await bg.CheckOnceAsync(CancellationToken.None);

        Assert.Empty(fake.InstallCallsReceived); // sanity: точно не устанавливали
        Assert.Equal(0, fake.SearchCallCount); // прямая проверка: поиск пропущен из-за ParanoidMode
        ProfileService.Current.ParanoidMode = false; // не оставлять состояние для других тестов
    }
}
