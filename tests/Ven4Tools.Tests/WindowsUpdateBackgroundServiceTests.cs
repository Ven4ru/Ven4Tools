using Ven4Tools.Models;
using Ven4Tools.Services;
using Ven4Tools.Services.WindowsUpdate;
using Ven4Tools.Tests.Fakes;

namespace Ven4Tools.Tests;

// Класс правит статический ProfileService.Current — общая коллекция с тестами,
// которые вдобавок сохраняют профиль на диск, исключает параллельный запуск.
[Collection("ProfileService")]
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

    // ── Фоновое скачивание (режим "NotifyAndDownload") ────────────────────────
    //
    // Проверяется ровно то, что отличает режим от "Только уведомлять": вызывается
    // DownloadOnlyAsync с правильным списком ID — и НИКОГДА не вызывается установка.
    // Реального обращения к Windows Update Agent здесь нет: источник подменён
    // FakeWindowsUpdateSource, живые патчи в систему не качаются.

    /// <summary>Готовит профиль под фоновую проверку и сбрасывает общее состояние.</summary>
    private static void ArrangeProfile(string mode)
    {
        ProfileService.Current.WindowsUpdateMode = mode;
        ProfileService.Current.ParanoidMode = false;
        ProfileService.Current.OfflineMode = false;
        WindowsUpdateBackgroundService.CountChangedResetForTests();
    }

    [Fact]
    public async Task CheckOnceAsync_ModeNotifyAndDownload_DownloadsFoundUpdates()
    {
        ArrangeProfile("NotifyAndDownload");
        var fake = new FakeWindowsUpdateSource();
        fake.Items.Add(new WindowsUpdateItem { UpdateId = "1", Title = "A" });
        fake.Items.Add(new WindowsUpdateItem { UpdateId = "2", Title = "B" });
        var bg = new WindowsUpdateBackgroundService(new WindowsUpdateService(fake));

        await bg.CheckOnceAsync(CancellationToken.None);

        Assert.Equal(1, fake.DownloadCallCount);
        Assert.Equal(new[] { "1", "2" }, fake.DownloadCallsReceived);
        // Железное правило приложения: фон никогда не устанавливает.
        Assert.Empty(fake.InstallCallsReceived);
    }

    [Fact]
    public async Task CheckOnceAsync_ModeNotifyOnly_DoesNotDownload()
    {
        ArrangeProfile("NotifyOnly");
        var fake = new FakeWindowsUpdateSource();
        fake.Items.Add(new WindowsUpdateItem { UpdateId = "1", Title = "A" });
        var bg = new WindowsUpdateBackgroundService(new WindowsUpdateService(fake));

        await bg.CheckOnceAsync(CancellationToken.None);

        Assert.Equal(1, fake.SearchCallCount); // поиск при этом выполнен — режим рабочий
        Assert.Equal(0, fake.DownloadCallCount);
        Assert.Empty(fake.InstallCallsReceived);
    }

    [Fact]
    public async Task CheckOnceAsync_ModeNotSet_DoesNotDownload()
    {
        ArrangeProfile("NotSet");
        var fake = new FakeWindowsUpdateSource();
        fake.Items.Add(new WindowsUpdateItem { UpdateId = "1", Title = "A" });
        var bg = new WindowsUpdateBackgroundService(new WindowsUpdateService(fake));

        await bg.CheckOnceAsync(CancellationToken.None);

        Assert.Equal(0, fake.SearchCallCount);
        Assert.Equal(0, fake.DownloadCallCount);
    }

    [Fact]
    public async Task CheckOnceAsync_ParanoidMode_DoesNotDownloadEvenInDownloadMode()
    {
        ArrangeProfile("NotifyAndDownload");
        ProfileService.Current.ParanoidMode = true;
        var fake = new FakeWindowsUpdateSource();
        fake.Items.Add(new WindowsUpdateItem { UpdateId = "1", Title = "A" });
        var bg = new WindowsUpdateBackgroundService(new WindowsUpdateService(fake));

        await bg.CheckOnceAsync(CancellationToken.None);

        Assert.Equal(0, fake.DownloadCallCount);
        Assert.Empty(fake.InstallCallsReceived);
        ProfileService.Current.ParanoidMode = false;
    }

    [Fact]
    public async Task CheckOnceAsync_OfflineMode_DoesNotDownload()
    {
        ArrangeProfile("NotifyAndDownload");
        ProfileService.Current.OfflineMode = true;
        var fake = new FakeWindowsUpdateSource();
        fake.Items.Add(new WindowsUpdateItem { UpdateId = "1", Title = "A" });
        var bg = new WindowsUpdateBackgroundService(new WindowsUpdateService(fake));

        await bg.CheckOnceAsync(CancellationToken.None);

        Assert.Equal(0, fake.DownloadCallCount);
        ProfileService.Current.OfflineMode = false;
    }

    [Fact]
    public async Task CheckOnceAsync_AllAlreadyDownloaded_DoesNotDownloadAgain()
    {
        ArrangeProfile("NotifyAndDownload");
        var fake = new FakeWindowsUpdateSource();
        fake.Items.Add(new WindowsUpdateItem { UpdateId = "1", Title = "A", IsDownloaded = true });
        fake.Items.Add(new WindowsUpdateItem { UpdateId = "2", Title = "B", IsDownloaded = true });
        var bg = new WindowsUpdateBackgroundService(new WindowsUpdateService(fake));

        await bg.CheckOnceAsync(CancellationToken.None);

        Assert.Equal(0, fake.DownloadCallCount);
    }

    [Fact]
    public async Task CheckOnceAsync_PartiallyDownloaded_TakesOnlyMissingOnes()
    {
        ArrangeProfile("NotifyAndDownload");
        var fake = new FakeWindowsUpdateSource();
        fake.Items.Add(new WindowsUpdateItem { UpdateId = "1", Title = "A", IsDownloaded = true });
        fake.Items.Add(new WindowsUpdateItem { UpdateId = "2", Title = "B" });
        var bg = new WindowsUpdateBackgroundService(new WindowsUpdateService(fake));

        await bg.CheckOnceAsync(CancellationToken.None);

        Assert.Equal(new[] { "2" }, fake.DownloadCallsReceived);
    }

    [Fact]
    public async Task CheckOnceAsync_NoUpdatesFound_DoesNotDownload()
    {
        ArrangeProfile("NotifyAndDownload");
        var fake = new FakeWindowsUpdateSource(); // пустой список патчей
        var bg = new WindowsUpdateBackgroundService(new WindowsUpdateService(fake));

        await bg.CheckOnceAsync(CancellationToken.None);

        Assert.Equal(1, fake.SearchCallCount);
        Assert.Equal(0, fake.DownloadCallCount);
    }

    [Fact]
    public async Task CheckOnceAsync_SearchFailed_DoesNotDownload()
    {
        ArrangeProfile("NotifyAndDownload");
        var fake = new FakeWindowsUpdateSource { SearchShouldFail = true, SearchFailureMessage = "тест" };
        fake.Items.Add(new WindowsUpdateItem { UpdateId = "1", Title = "A" });
        var bg = new WindowsUpdateBackgroundService(new WindowsUpdateService(fake));

        await bg.CheckOnceAsync(CancellationToken.None);

        Assert.Equal(0, fake.DownloadCallCount);
    }

    [Fact]
    public async Task CheckOnceAsync_DownloadFailed_DoesNotThrowAndDoesNotInstall()
    {
        ArrangeProfile("NotifyAndDownload");
        var fake = new FakeWindowsUpdateSource { DownloadShouldFailOutright = true };
        fake.Items.Add(new WindowsUpdateItem { UpdateId = "1", Title = "A" });
        var bg = new WindowsUpdateBackgroundService(new WindowsUpdateService(fake));

        // Сбой фоновой загрузки не должен всплывать исключением наружу — цикл живёт дальше.
        await bg.CheckOnceAsync(CancellationToken.None);

        Assert.Equal(1, fake.DownloadCallCount);
        Assert.Empty(fake.InstallCallsReceived);
    }
}
