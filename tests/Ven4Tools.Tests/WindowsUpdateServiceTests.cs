using Ven4Tools.Services;
using Ven4Tools.Services.WindowsUpdate;
using Ven4Tools.Tests.Fakes;

namespace Ven4Tools.Tests;

[CollectionDefinition("InstallSemaphore")]
public class InstallSemaphoreCollection { }

[Collection("InstallSemaphore")]
public sealed class WindowsUpdateServiceTests
{
    [Fact]
    public async Task InstallSelectedAsync_EmptyList_ReturnsFailureWithoutTouchingSource()
    {
        var fake = new FakeWindowsUpdateSource();
        var service = new WindowsUpdateService(fake);

        var result = await service.InstallSelectedAsync(
            Array.Empty<string>(), new Progress<WindowsUpdateProgress>(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Empty(fake.InstallCallsReceived);
    }

    [Fact]
    public async Task InstallSelectedAsync_RebootPending_ReturnsFailureWithoutInstalling()
    {
        var fake = new FakeWindowsUpdateSource { RebootPending = true };
        fake.Items.Add(new WindowsUpdateItem { UpdateId = "1", Title = "A" });
        var service = new WindowsUpdateService(fake);

        var result = await service.InstallSelectedAsync(
            new[] { "1" }, new Progress<WindowsUpdateProgress>(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("перезагрузка", result.ErrorMessage);
        Assert.Empty(fake.InstallCallsReceived);
    }

    [Fact]
    public async Task InstallSelectedAsync_CatalogInstallInProgress_ReturnsFailureWithoutInstalling()
    {
        var fake = new FakeWindowsUpdateSource();
        fake.Items.Add(new WindowsUpdateItem { UpdateId = "1", Title = "A" });
        var service = new WindowsUpdateService(fake);

        await InstallationService.InstallSemaphore.WaitAsync();
        try
        {
            var result = await service.InstallSelectedAsync(
                new[] { "1" }, new Progress<WindowsUpdateProgress>(), CancellationToken.None);

            Assert.False(result.Success);
            Assert.Contains("каталога", result.ErrorMessage);
            Assert.Empty(fake.InstallCallsReceived);
        }
        finally
        {
            InstallationService.InstallSemaphore.Release();
        }
    }

    [Fact]
    public async Task InstallSelectedAsync_HappyPath_CallsSourceAndReleasesSemaphore()
    {
        var fake = new FakeWindowsUpdateSource();
        fake.Items.Add(new WindowsUpdateItem { UpdateId = "1", Title = "A" });
        var service = new WindowsUpdateService(fake);

        var result = await service.InstallSelectedAsync(
            new[] { "1" }, new Progress<WindowsUpdateProgress>(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(fake.InstallCallsReceived);
        Assert.False(InstallationService.IsBusy); // семафор освобождён
    }

    /// <summary>
    /// Главная проверка разделения семафоров: пока идёт фоновое скачивание патчей,
    /// общий семафор установки СВОБОДЕН. Иначе каталог, история и полоса закреплённых
    /// приложений на десятки минут блокировались бы ложным «дождитесь завершения
    /// текущей установки», хотя ничего не устанавливается.
    /// </summary>
    [Fact]
    public async Task DownloadOnlyAsync_WhileRunning_DoesNotHoldInstallSemaphore()
    {
        var fake = new FakeWindowsUpdateSource();
        fake.Items.Add(new WindowsUpdateItem { UpdateId = "1", Title = "A" });
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.DownloadStarted = started;
        fake.DownloadRelease = release.Task;
        var service = new WindowsUpdateService(fake);

        var download = service.DownloadOnlyAsync(
            new[] { "1" }, new Progress<WindowsUpdateProgress>(), CancellationToken.None);
        await started.Task;

        Assert.False(InstallationService.IsBusy);
        Assert.True(await InstallationService.InstallSemaphore.WaitAsync(0)); // реально свободен
        InstallationService.InstallSemaphore.Release();

        // При этом сама WU-подсистема занята — установку патчей начинать нельзя.
        Assert.True(WindowsUpdateService.IsWindowsUpdateBusy);
        Assert.True(WindowsUpdateService.IsDownloadingInBackground);

        release.SetResult(true);
        var outcome = await download;

        Assert.NotEmpty(outcome.Items);
        Assert.All(outcome.Items, i => Assert.True(i.Success));
        Assert.False(WindowsUpdateService.IsWindowsUpdateBusy);
        Assert.False(WindowsUpdateService.IsDownloadingInBackground);
    }

    /// <summary>
    /// Обратное направление той же развязки: идущая установка приложений из каталога
    /// больше не отменяет фоновое скачивание патчей — msiexec и загрузчик WUA друг
    /// другу не мешают.
    /// </summary>
    [Fact]
    public async Task DownloadOnlyAsync_CatalogInstallInProgress_StillDownloads()
    {
        var fake = new FakeWindowsUpdateSource();
        fake.Items.Add(new WindowsUpdateItem { UpdateId = "1", Title = "A" });
        var service = new WindowsUpdateService(fake);

        await InstallationService.InstallSemaphore.WaitAsync();
        try
        {
            var outcome = await service.DownloadOnlyAsync(
                new[] { "1" }, new Progress<WindowsUpdateProgress>(), CancellationToken.None);

            Assert.NotEmpty(outcome.Items);
            Assert.All(outcome.Items, i => Assert.True(i.Success));
            Assert.Equal(1, fake.DownloadCallCount);
        }
        finally
        {
            InstallationService.InstallSemaphore.Release();
        }
    }

    /// <summary>
    /// Установка патчей во время фонового скачивания отклоняется — но с честным
    /// сообщением про скачивание, а не «дождитесь установки приложений из каталога».
    /// </summary>
    [Fact]
    public async Task InstallSelectedAsync_BackgroundDownloadInProgress_ReturnsDownloadSpecificMessage()
    {
        var downloadFake = new FakeWindowsUpdateSource();
        downloadFake.Items.Add(new WindowsUpdateItem { UpdateId = "1", Title = "A" });
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        downloadFake.DownloadStarted = started;
        downloadFake.DownloadRelease = release.Task;

        var download = new WindowsUpdateService(downloadFake).DownloadOnlyAsync(
            new[] { "1" }, new Progress<WindowsUpdateProgress>(), CancellationToken.None);
        await started.Task;

        try
        {
            var installFake = new FakeWindowsUpdateSource();
            installFake.Items.Add(new WindowsUpdateItem { UpdateId = "2", Title = "B" });

            var result = await new WindowsUpdateService(installFake).InstallSelectedAsync(
                new[] { "2" }, new Progress<WindowsUpdateProgress>(), CancellationToken.None);

            Assert.False(result.Success);
            Assert.Contains("скачивание", result.ErrorMessage);
            Assert.DoesNotContain("каталога", result.ErrorMessage);
            Assert.Empty(installFake.InstallCallsReceived);
        }
        finally
        {
            release.SetResult(true);
            await download;
        }
    }
}
