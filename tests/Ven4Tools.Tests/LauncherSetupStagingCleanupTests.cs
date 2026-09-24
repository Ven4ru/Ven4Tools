using System;
using System.IO;
using Ven4Tools.Launcher.Services;
using Xunit;

namespace Ven4Tools.Tests;

// Папки прошлых загрузок установщика лаунчера в %TEMP%: после успешного
// самообновления установщик на 48 МБ оставался там навсегда.
public sealed class LauncherSetupStagingCleanupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "v4t_staging_test_" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Now = new(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);

    public LauncherSetupStagingCleanupTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string MakeDir(string name, TimeSpan age, string? file = null)
    {
        string dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        if (file != null) File.WriteAllText(Path.Combine(dir, file), "x");
        Directory.SetLastWriteTimeUtc(dir, Now - age);
        return dir;
    }

    [Fact]
    public void СтарыеПапкиУстановщика_Удаляются()
    {
        string setup = MakeDir(LauncherUpdateInstaller.StagingPrefix + "aaa", TimeSpan.FromDays(3), "Ven4Tools.Setup-3.7.0.exe");
        string partial = MakeDir(LauncherUpdateInstaller.StagingPrefix + "bbb", TimeSpan.FromDays(14), "Ven4Tools.Setup-3.6.0.exe.partial");

        int removed = LauncherUpdateInstaller.CleanupStaleStagingDirectories(_root, Now, TimeSpan.FromHours(6));

        Assert.Equal(2, removed);
        Assert.False(Directory.Exists(setup));
        Assert.False(Directory.Exists(partial));
    }

    [Fact]
    public void СвежаяПапка_Остаётся()
    {
        // Установщик, запущенный недавно, может ещё работать из своей папки.
        string fresh = MakeDir(LauncherUpdateInstaller.StagingPrefix + "ccc", TimeSpan.FromMinutes(10), "Ven4Tools.Setup-3.7.0.exe");

        int removed = LauncherUpdateInstaller.CleanupStaleStagingDirectories(_root, Now, TimeSpan.FromHours(6));

        Assert.Equal(0, removed);
        Assert.True(Directory.Exists(fresh));
    }

    [Fact]
    public void ЧужиеПапки_НеТрогаются()
    {
        string foreign = MakeDir("someone_else_setup_ddd", TimeSpan.FromDays(30), "file.exe");

        int removed = LauncherUpdateInstaller.CleanupStaleStagingDirectories(_root, Now, TimeSpan.FromHours(6));

        Assert.Equal(0, removed);
        Assert.True(Directory.Exists(foreign));
    }

    [Fact]
    public void ЗанятыйФайл_ПапкаПропускаетсяБезИсключения()
    {
        string busy = MakeDir(LauncherUpdateInstaller.StagingPrefix + "eee", TimeSpan.FromDays(3), "Ven4Tools.Setup-3.7.0.exe");
        using var handle = new FileStream(Path.Combine(busy, "Ven4Tools.Setup-3.7.0.exe"), FileMode.Open, FileAccess.Read, FileShare.None);

        int removed = LauncherUpdateInstaller.CleanupStaleStagingDirectories(_root, Now, TimeSpan.FromHours(6));

        Assert.Equal(0, removed);
        Assert.True(Directory.Exists(busy));
    }

    [Fact]
    public void НесуществующийКорень_Ноль()
    {
        Assert.Equal(0, LauncherUpdateInstaller.CleanupStaleStagingDirectories(
            Path.Combine(_root, "нет"), Now, TimeSpan.FromHours(6)));
    }
}
