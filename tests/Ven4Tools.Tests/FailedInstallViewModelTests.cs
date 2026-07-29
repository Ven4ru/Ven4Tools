using Ven4Tools.Services;
using Ven4Tools.ViewModels;

namespace Ven4Tools.Tests;

// Тот же общий семафор установки, что и у остальных тестов занятости.
[Collection("InstallSemaphore")]
public sealed class FailedInstallViewModelTests
{
    private static FailedInstallViewModel Create(Func<FailedInstallViewModel, Task> retry) =>
        new("7-Zip", "Winget", "Код выхода — ошибка", retry);

    [Fact]
    public async Task Retry_IsBlocked_WhileAnotherInstallRuns()
    {
        bool invoked = false;
        var vm = Create(_ => { invoked = true; return Task.CompletedTask; });

        await InstallationService.InstallSemaphore.WaitAsync();
        try
        {
            Assert.False(vm.RetryCommand.CanExecute(null));

            // Программный вызов команды тоже не должен пройти: кнопка «Повторить»
            // обязана уважать общий семафор, а не только визуально гаснуть.
            vm.RetryCommand.Execute(null);
            Assert.False(invoked);
        }
        finally
        {
            InstallationService.InstallSemaphore.Release();
        }

        Assert.True(vm.RetryCommand.CanExecute(null));
    }

    [Fact]
    public async Task Retry_RunsOwnersInstallPath_WhenSemaphoreIsFree()
    {
        var invoked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = Create(item =>
        {
            item.RetryStatus = "⏳ Повторная установка...";
            invoked.TrySetResult(true);
            return Task.CompletedTask;
        });

        Assert.True(vm.RetryCommand.CanExecute(null));
        vm.RetryCommand.Execute(null);

        Assert.True(await invoked.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("⏳ Повторная установка...", vm.RetryStatus);
        // Повтор завершён — команда снова доступна.
        Assert.False(vm.IsRetrying);
    }

    [Fact]
    public async Task Retry_SurvivesFailingRetryCallback()
    {
        var vm = Create(_ => throw new InvalidOperationException("сеть недоступна"));

        vm.RetryCommand.Execute(null);
        await Task.Delay(50);

        // Команда выполняется как async void — исключение не должно ронять клиент,
        // пользователь видит причину и может попробовать снова.
        Assert.False(vm.IsRetrying);
        Assert.Contains("сеть недоступна", vm.RetryStatus, StringComparison.Ordinal);
        Assert.True(vm.RetryCommand.CanExecute(null));
    }

    [Fact]
    public void UpdateFailure_RefreshesMethodAndReason()
    {
        var vm = Create(_ => Task.CompletedTask);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.UpdateFailure("Chocolatey", "Пакет не найден");

        Assert.Equal("Chocolatey", vm.Method);
        Assert.Equal("Способ: Chocolatey", vm.MethodText);
        Assert.Equal("Пакет не найден", vm.Error);
        Assert.Contains(nameof(FailedInstallViewModel.MethodText), changed);
        Assert.Contains(nameof(FailedInstallViewModel.Error), changed);
    }
}
