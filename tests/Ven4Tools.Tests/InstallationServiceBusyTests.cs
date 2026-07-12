using Ven4Tools.Services;

namespace Ven4Tools.Tests;

[Collection("InstallSemaphore")]
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

    [Fact]
    public async Task InstallSemaphore_BlocksSecondEntry_WhileHeld()
    {
        // Массовый импорт (BtnImport_Click) обязан выходить по IsBusy и захватывать
        // этот же семафор. Проверяем инвариант, на который опирается ранний выход:
        // пока семафор занят, повторный вход не проходит (нет параллельного msiexec).
        await InstallationService.InstallSemaphore.WaitAsync();
        try
        {
            Assert.True(InstallationService.IsBusy);

            bool secondEntry = await InstallationService.InstallSemaphore.WaitAsync(0);
            Assert.False(secondEntry);
        }
        finally
        {
            InstallationService.InstallSemaphore.Release();
        }

        Assert.False(InstallationService.IsBusy);

        // Семафор снова свободен — вход проходит и корректно освобождается.
        Assert.True(await InstallationService.InstallSemaphore.WaitAsync(0));
        InstallationService.InstallSemaphore.Release();
    }
}
