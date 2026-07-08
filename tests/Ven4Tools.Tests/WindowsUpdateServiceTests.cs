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
}
