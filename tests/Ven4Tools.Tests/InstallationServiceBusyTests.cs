using Ven4Tools.Services;

namespace Ven4Tools.Tests;

public sealed class InstallationServiceBusyTests
{
    [Fact]
    public async Task IsBusy_ReflectsSemaphoreState()
    {
        // Семафор статический и общий на процесс — тест синхронный по семафору,
        // чтобы не мешать параллельным тестам, использующим тот же семафор.
        await InstallationService.InstallSemaphore.WaitAsync();
        try
        {
            Assert.True(InstallationService.IsBusy);
        }
        finally
        {
            InstallationService.InstallSemaphore.Release();
        }

        Assert.False(InstallationService.IsBusy);
    }
}
