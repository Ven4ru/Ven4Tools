using Ven4Tools.Helpers;
using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Tests;

/// <summary>
/// Уборка временных файлов, оставшихся после убитого процесса: удаляется только
/// своё (по шаблону имени), только старое и только не каталог текущего процесса.
/// </summary>
public sealed class TempArtifactsCleanupTests
{
    private static readonly DateTime Now = new(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan MinAge = TimeSpan.FromHours(6);

    private static string MakeDirectory(string root, string name, DateTime lastWriteUtc)
    {
        string path = Path.Combine(root, name);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "payload.bin"), "x");
        Directory.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }

    private static string MakeFile(string root, string name, DateTime lastWriteUtc)
    {
        string path = Path.Combine(root, name);
        File.WriteAllText(path, "x");
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }

    [Fact]
    public void Лаунчер_УдаляетСтарыеСвоиПапкиИФайлы()
    {
        using var temp = new TemporaryDirectory();
        var old = Now.AddHours(-7);
        string delta = MakeDirectory(temp.Path, "Ven4Tools_Delta_5.3.0_0123456789abcdef0123456789abcdef", old);
        string repair = MakeDirectory(temp.Path, "Ven4Tools_Repair_0123456789abcdef0123456789abcdef", old);
        string setup = MakeDirectory(temp.Path, "ven4tools_setup_0123456789abcdef0123456789abcdef", old);
        string installers = MakeDirectory(temp.Path, "Ven4Tools.Launcher.Installers.0123456789abcdef0123456789abcdef", old);
        string archive = MakeFile(temp.Path, "Ven4Tools_Client_5.3.0_0123456789abcdef0123456789abcdef.zip", old);
        string partial = MakeFile(temp.Path, "Ven4Tools_Client_5.3.0_fedcba9876543210fedcba9876543210.zip.partial", old);

        int removed = LauncherUpdateInstaller.CleanupStaleTempArtifacts(temp.Path, Now, MinAge);

        Assert.Equal(6, removed);
        Assert.False(Directory.Exists(delta));
        Assert.False(Directory.Exists(repair));
        Assert.False(Directory.Exists(setup));
        Assert.False(Directory.Exists(installers));
        Assert.False(File.Exists(archive));
        Assert.False(File.Exists(partial));
    }

    [Fact]
    public void Лаунчер_НеТрогаетСвежееИЧужое()
    {
        using var temp = new TemporaryDirectory();
        var old = Now.AddHours(-7);
        string fresh = MakeDirectory(temp.Path, "Ven4Tools_Delta_5.3.0_0123456789abcdef0123456789abcdef", Now.AddHours(-1));
        string foreignDirectory = MakeDirectory(temp.Path, "Ven4Tools_Backup", old);
        string foreignFile = MakeFile(temp.Path, "Ven4Tools_Client_notes.txt", old);
        string freshArchive = MakeFile(temp.Path, "Ven4Tools_Client_5.3.0_0123456789abcdef0123456789abcdef.zip", Now.AddMinutes(-5));

        int removed = LauncherUpdateInstaller.CleanupStaleTempArtifacts(temp.Path, Now, MinAge);

        Assert.Equal(0, removed);
        Assert.True(Directory.Exists(fresh));
        Assert.True(Directory.Exists(foreignDirectory));
        Assert.True(File.Exists(foreignFile));
        Assert.True(File.Exists(freshArchive));
    }

    [Fact]
    public void Лаунчер_НесуществующийКорень_НеПадает()
    {
        using var temp = new TemporaryDirectory();

        Assert.Equal(0, LauncherUpdateInstaller.CleanupStaleTempArtifacts(
            Path.Combine(temp.Path, "нет"), Now, MinAge));
    }

    [Fact]
    public void Клиент_УдаляетКаталогиПрошлыхСеансов_КромеСвоегоИСвежих()
    {
        using var temp = new TemporaryDirectory();
        var old = Now.AddHours(-7);
        string stale = MakeDirectory(temp.Path, "Ven4Tools.Installers.0123456789abcdef0123456789abcdef", old);
        string staleLauncher = MakeDirectory(temp.Path, "Ven4Tools.Launcher.Installers.0123456789abcdef0123456789abcdef", old);
        string current = MakeDirectory(temp.Path, "Ven4Tools.Installers.fedcba9876543210fedcba9876543210", old);
        string fresh = MakeDirectory(temp.Path, "Ven4Tools.Installers.00000000000000000000000000000000", Now.AddMinutes(-10));
        string foreign = MakeDirectory(temp.Path, "Ven4Tools.Other", old);

        int removed = InstallerTempDirectory.CleanupStale(new[] { temp.Path }, current, Now, MinAge);

        Assert.Equal(2, removed);
        Assert.False(Directory.Exists(stale));
        Assert.False(Directory.Exists(staleLauncher));
        Assert.True(Directory.Exists(current));
        Assert.True(Directory.Exists(fresh));
        Assert.True(Directory.Exists(foreign));
    }
}
