using Ven4Tools.Services;

namespace Ven4Tools.Tests;

// Проверяет ровно тот механизм, который PackageManagerService.RunChocoInstallAsync
// и InstallChocoAsync используют для собственного дедлайна поверх внешнего
// CancellationToken. До этого фикса вызывающий код ждал choco.exe БЕЗ какого-либо
// внутреннего таймаута — только внешняя отмена пользователем (RunChocoInstallAsync)
// или вообще ничего (InstallChocoAsync — токен даже не принимался параметром).
// Живое подтверждение самой проблемы — chocolatey.log за 2026-07-05: hwmonitor и
// cpu-z зависли на 45 минут каждый (download.cpuid.com), и наш процесс молча ждал
// весь этот срок. Здесь тестируется чистая логика композиции токенов — без
// реального Process/choco.exe, детерминированно и быстро.
public sealed class PackageManagerServiceTimeoutTests
{
    [Fact]
    public async Task LinkedToken_CancelsOnInternalTimeout_WhenExternalTokenNeverFires()
    {
        var (timeoutCts, linkedCts) = PackageManagerService.CreateInstallTimeoutTokens(
            CancellationToken.None, TimeSpan.FromMilliseconds(50));
        using (timeoutCts)
        using (linkedCts)
        {
            // Операция, которая сама по себе никогда не завершится (моделирует
            // зависший choco.exe) — единственное, что может её прервать, это токен.
            var neverCompletes = Task.Delay(Timeout.Infinite, linkedCts.Token);

            var completed = await Task.WhenAny(neverCompletes, Task.Delay(TimeSpan.FromSeconds(5)));

            Assert.Same(neverCompletes, completed);
            Assert.True(linkedCts.IsCancellationRequested);
            Assert.True(timeoutCts.IsCancellationRequested);
            // Именно ВНУТРЕННИЙ таймаут сработал, а не внешняя отмена —
            // вызывающий код обязан показать «таймаут», а не «отменено».
            Assert.True(PackageManagerService.IsTimeoutNotCancellation(timeoutCts, CancellationToken.None));
        }
    }

    [Fact]
    public async Task LinkedToken_CancelsImmediately_WhenExternalTokenFiresFirst()
    {
        using var externalCts = new CancellationTokenSource();
        var (timeoutCts, linkedCts) = PackageManagerService.CreateInstallTimeoutTokens(
            externalCts.Token, TimeSpan.FromMinutes(10));
        using (timeoutCts)
        using (linkedCts)
        {
            externalCts.Cancel();

            var neverCompletes = Task.Delay(Timeout.Infinite, linkedCts.Token);
            var completed = await Task.WhenAny(neverCompletes, Task.Delay(TimeSpan.FromSeconds(2)));

            Assert.Same(neverCompletes, completed);
            Assert.True(linkedCts.IsCancellationRequested);
            // Отмена пользователем, не таймаут — 10-минутный внутренний дедлайн ещё
            // даже близко не истёк.
            Assert.False(PackageManagerService.IsTimeoutNotCancellation(timeoutCts, externalCts.Token));
        }
    }

    [Fact]
    public async Task LinkedToken_DoesNotCancel_WhenOperationFinishesBeforeTimeout()
    {
        var (timeoutCts, linkedCts) = PackageManagerService.CreateInstallTimeoutTokens(
            CancellationToken.None, TimeSpan.FromSeconds(30));
        using (timeoutCts)
        using (linkedCts)
        {
            await Task.Delay(20, linkedCts.Token);

            Assert.False(linkedCts.IsCancellationRequested);
            Assert.False(timeoutCts.IsCancellationRequested);
        }
    }

    // ── Инвалидация ACL-кэша после InstallChocoAsync ────────────────────────────

    [Fact]
    public void InvalidateAclCache_RemovesCachedEntry()
    {
        using var dir = new TemporaryDirectory();

        Assert.False(TrustedExecutablePaths.IsAclCacheEntryCached(dir.Path));
        _ = TrustedExecutablePaths.IsDirectoryAclCompromised(dir.Path); // популяризует кэш
        Assert.True(TrustedExecutablePaths.IsAclCacheEntryCached(dir.Path));

        TrustedExecutablePaths.InvalidateAclCache(dir.Path);

        Assert.False(TrustedExecutablePaths.IsAclCacheEntryCached(dir.Path));
    }

    [Fact]
    public void InvalidateChocolateyAclCache_ClearsEntryForChocolateyBinDirectory()
    {
        string chocoBin = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "chocolatey", "bin");

        _ = TrustedExecutablePaths.IsDirectoryAclCompromised(chocoBin); // популяризует кэш
        Assert.True(TrustedExecutablePaths.IsAclCacheEntryCached(chocoBin));

        TrustedExecutablePaths.InvalidateChocolateyAclCache();

        Assert.False(TrustedExecutablePaths.IsAclCacheEntryCached(chocoBin));
    }
}
