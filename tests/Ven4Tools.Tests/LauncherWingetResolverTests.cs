using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Tests;

/// <summary>
/// Резолвинг winget в лаунчере. Закрывает две находки 2026-09-10, из-за которых
/// «Установить компоненты» могло бесконечно сообщать «Winget не установлен»:
///   1. отрицательный результат поиска пакета кэшировался на весь процесс —
///      установив winget сам, лаунчер до перезапуска его «не видел»;
///   2. каталог пакета искался только перечислением Program Files\WindowsApps,
///      которое обычному (не-elevated) пользователю запрещено, поэтому вся
///      детекция держалась на псевдониме %LocalAppData%\Microsoft\WindowsApps,
///      которого может не быть.
/// </summary>
public sealed class LauncherWingetResolverTests
{
    private static string AliasDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Microsoft", "WindowsApps");

    private static string WindowsAppsRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");

    [Fact]
    public void InvalidateWingetCache_ClearsAclEntryForAliasDirectory()
    {
        _ = TrustedExecutablePaths.IsDirectoryAclCompromised(AliasDirectory); // популяризует кэш
        Assert.True(TrustedExecutablePaths.IsAclCacheEntryCached(AliasDirectory));

        TrustedExecutablePaths.InvalidateWingetCache();

        Assert.False(TrustedExecutablePaths.IsAclCacheEntryCached(AliasDirectory));
    }

    /// <summary>
    /// Ровно та регрессия, из-за которой лаунчер не замечал только что
    /// установленный им winget: сброс кэша не должен «терять» пакет, который на
    /// машине есть. На агенте без App Installer проверять нечего — тест
    /// подтверждает лишь согласованность до и после сброса.
    /// </summary>
    [Fact]
    public void ResolveWinget_SurvivesCacheInvalidation()
    {
        string? before = TrustedExecutablePaths.ResolveWinget();

        TrustedExecutablePaths.InvalidateWingetCache();

        Assert.Equal(before, TrustedExecutablePaths.ResolveWinget());
    }

    /// <summary>
    /// Путь из реестра AppModel — подсказка из ветки, доступной пользователю на
    /// запись. Единственное, что мешает подсунуть через неё свой каталог, —
    /// требование лежать внутри Program Files\WindowsApps (защищён TrustedInstaller).
    /// </summary>
    [Theory]
    [InlineData(@"C:\Users\Public\winget")]
    [InlineData(@"C:\Program Files\WindowsAppsEvil\Microsoft.DesktopAppInstaller_1.0.0.0_x64__8wekyb3d8bbwe")]
    [InlineData(@"C:\Windows\Temp")]
    public void IsInsideWindowsApps_RejectsPathsOutsideProtectedRoot(string path)
    {
        Assert.False(TrustedExecutablePaths.IsInsideWindowsApps(path));
    }

    [Fact]
    public void IsInsideWindowsApps_RejectsRootItself()
    {
        Assert.False(TrustedExecutablePaths.IsInsideWindowsApps(WindowsAppsRoot));
    }

    [Fact]
    public void IsInsideWindowsApps_RejectsTraversalBackOutOfRoot()
    {
        Assert.False(TrustedExecutablePaths.IsInsideWindowsApps(
            Path.Combine(WindowsAppsRoot, "..", "..", "Users", "Public", "winget")));
    }

    [Fact]
    public void IsInsideWindowsApps_AcceptsPackageFolderUnderRoot()
    {
        Assert.True(TrustedExecutablePaths.IsInsideWindowsApps(
            Path.Combine(WindowsAppsRoot, "Microsoft.DesktopAppInstaller_1.29.290.0_x64__8wekyb3d8bbwe")));
    }

    /// <summary>
    /// Резолвер отдаёт либо каталог пакета под защищённым корнем, либо псевдоним
    /// в профиле пользователя — третьего варианта быть не должно ни при каких
    /// данных в реестре.
    /// </summary>
    [Fact]
    public void ResolveWinget_ReturnsOnlyTrustedLocations()
    {
        string? resolved = TrustedExecutablePaths.ResolveWinget();
        if (resolved == null) return; // на этой машине winget не установлен

        Assert.Equal("winget.exe", Path.GetFileName(resolved));
        Assert.True(
            TrustedExecutablePaths.IsInsideWindowsApps(resolved) ||
            string.Equals(
                Path.GetDirectoryName(resolved),
                AliasDirectory,
                StringComparison.OrdinalIgnoreCase),
            $"Неожиданное расположение winget.exe: {resolved}");
    }
}
