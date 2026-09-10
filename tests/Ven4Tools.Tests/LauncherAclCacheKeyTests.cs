using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Tests;

/// <summary>
/// Ключи кэша ACL в лаунчере. Клиентская копия класса нормализовала путь и
/// сравнивала без учёта регистра с самого начала, лаунчерная — нет: один и тот же
/// каталог, записанный с завершающим слэшем и без него (или в другом регистре),
/// занимал два слота. Практическое следствие — не лишний вызов GetAccessControl,
/// а то, что <see cref="TrustedExecutablePaths.InvalidateAclCache"/> по одному
/// написанию не сбрасывал вердикт, закэшированный по другому: после установки
/// Chocolatey/winget резолвер мог продолжать отвечать по устаревшему ACL.
/// </summary>
public sealed class LauncherAclCacheKeyTests
{
    [Fact]
    public void TrailingSeparator_HitsTheSameCacheEntry()
    {
        using var dir = new TemporaryDirectory();
        TrustedExecutablePaths.InvalidateAclCache(dir.Path);

        _ = TrustedExecutablePaths.IsDirectoryAclCompromised(dir.Path);

        Assert.True(TrustedExecutablePaths.IsAclCacheEntryCached(dir.Path + "\\"));
    }

    [Fact]
    public void DifferentCase_HitsTheSameCacheEntry()
    {
        using var dir = new TemporaryDirectory();
        TrustedExecutablePaths.InvalidateAclCache(dir.Path);

        _ = TrustedExecutablePaths.IsDirectoryAclCompromised(dir.Path);

        Assert.True(TrustedExecutablePaths.IsAclCacheEntryCached(dir.Path.ToUpperInvariant()));
    }

    [Fact]
    public void Invalidate_WithTrailingSeparator_ClearsEntryStoredWithout()
    {
        using var dir = new TemporaryDirectory();
        _ = TrustedExecutablePaths.IsDirectoryAclCompromised(dir.Path);
        Assert.True(TrustedExecutablePaths.IsAclCacheEntryCached(dir.Path));

        TrustedExecutablePaths.InvalidateAclCache(dir.Path + "\\");

        Assert.False(TrustedExecutablePaths.IsAclCacheEntryCached(dir.Path));
    }

    [Fact]
    public void Invalidate_ClearsBothStrictAndLenientVerdicts()
    {
        using var dir = new TemporaryDirectory();
        _ = TrustedExecutablePaths.IsDirectoryAclCompromised(dir.Path);
        _ = TrustedExecutablePaths.IsDirectoryWritableByOtherUsers(dir.Path);

        TrustedExecutablePaths.InvalidateAclCache(dir.Path.ToUpperInvariant() + "\\");

        Assert.False(TrustedExecutablePaths.IsAclCacheEntryCached(dir.Path));
    }
}
