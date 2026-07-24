using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Tests;

// Согласовано с PackageManagerServiceTimeoutTests (клиент) — та же инвалидация
// ACL-кэша, отдельная копия для лаунчера (см. MainWindow.PackageManagers.cs
// InstallChocoAsync и TrustedExecutablePaths.InvalidateChocolateyAclCache там же).
public sealed class LauncherTrustedExecutablePathsAclCacheTests
{
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
