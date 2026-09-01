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

        // try/finally обязателен: без него упавшая проверка оставила бы фейк
        // висеть навсегда, WU-семафор — захваченным до конца прогона, и все
        // последующие WU-тесты падали бы по ложной причине.
        try
        {
            Assert.False(InstallationService.IsBusy);
            Assert.True(await InstallationService.InstallSemaphore.WaitAsync(0)); // реально свободен
            InstallationService.InstallSemaphore.Release();

            // При этом сама WU-подсистема занята — установку патчей начинать нельзя.
            Assert.True(WindowsUpdateService.IsWindowsUpdateBusy);
            Assert.True(WindowsUpdateService.IsDownloadingInBackground);
        }
        finally
        {
            release.TrySetResult(true);
        }

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
            release.TrySetResult(true);
            await download;
        }
    }

    /// <summary>
    /// Гонка «проверил — а занялось после»: фоновое скачивание захватывает WU-семафор
    /// уже ПОСЛЕ проверки занятости в <see cref="WindowsUpdateService.InstallSelectedAsync"/>,
    /// но ещё до захвата семафоров. Установка обязана отказать сразу, а не встать в
    /// блокирующее ожидание, удерживая при этом общий семафор приложения: иначе каталог,
    /// история и пины на всё время загрузки патча получают ложное «дождитесь завершения
    /// текущей установки» — ровно та регрессия, ради устранения которой семафоры разводились.
    /// </summary>
    [Fact]
    public async Task InstallSelectedAsync_DownloadStartsAfterBusyCheck_FailsFastWithoutHoldingInstallSemaphore()
    {
        var downloadFake = new FakeWindowsUpdateSource();
        downloadFake.Items.Add(new WindowsUpdateItem { UpdateId = "1", Title = "A" });
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        downloadFake.DownloadStarted = started;
        downloadFake.DownloadRelease = release.Task;

        var installFake = new FakeWindowsUpdateSource();
        installFake.Items.Add(new WindowsUpdateItem { UpdateId = "2", Title = "B" });

        Task<WindowsUpdateDownloadOutcome>? download = null;
        installFake.OnRebootPendingChecked = () =>
        {
            if (download != null) return;
            download = new WindowsUpdateService(downloadFake).DownloadOnlyAsync(
                new[] { "1" }, new Progress<WindowsUpdateProgress>(), CancellationToken.None);
            started.Task.GetAwaiter().GetResult(); // скачивание уже держит WU-семафор
        };

        try
        {
            // Таймаут, а не бесконечное ожидание: при возврате блокирующего захвата
            // WU-семафора этот вызов не завершился бы никогда, и падение теста должно
            // быть внятным, а не зависанием всего прогона.
            var result = await new WindowsUpdateService(installFake)
                .InstallSelectedAsync(
                    new[] { "2" }, new Progress<WindowsUpdateProgress>(), CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(10));

            Assert.False(result.Success);
            Assert.Contains("скачивание", result.ErrorMessage);
            Assert.Empty(installFake.InstallCallsReceived);

            // Скачивание всё ещё идёт — значит отказ случился ВО ВРЕМЯ него, а не после.
            Assert.True(WindowsUpdateService.IsDownloadingInBackground);

            // И главное: общий семафор приложения отпущен, каталог/история/пины свободны.
            Assert.False(InstallationService.IsBusy);
            Assert.True(await InstallationService.InstallSemaphore.WaitAsync(0));
            InstallationService.InstallSemaphore.Release();
        }
        finally
        {
            release.TrySetResult(true);
            if (download != null) await download;
        }
    }
}
