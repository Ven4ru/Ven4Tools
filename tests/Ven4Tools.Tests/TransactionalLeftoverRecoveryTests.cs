using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Tests;

/// <summary>
/// Разбор остатков InstallPartial после убитого процесса или отключения питания.
/// Главное — не оставить в папке клиента смесь двух версий: операция, прерванная
/// посреди фиксации, откатывается целиком.
/// </summary>
public sealed class TransactionalLeftoverRecoveryTests
{
    private const string Op = "0123456789abcdef0123456789abcdef";

    private static void Write(string dir, string name, string content) =>
        File.WriteAllText(Path.Combine(dir, name), content);

    private static string Read(string dir, string name) =>
        File.ReadAllText(Path.Combine(dir, name));

    [Fact]
    public void ПрерваннаяФиксация_ОткатываетсяЦеликом()
    {
        using var area = new TemporaryDirectory();
        // a.dll уже заменён (оригинал в .old), b.dll ещё нет (новая версия в .new).
        Write(area.Path, "a.dll", "new-a");
        Write(area.Path, $"a.dll.old-{Op}", "old-a");
        Write(area.Path, "b.dll", "old-b");
        Write(area.Path, $"b.dll.new-{Op}", "new-b");

        var (restored, removed) = TransactionalDirectoryInstaller.RecoverLeftovers(area.Path);

        Assert.Equal("old-a", Read(area.Path, "a.dll"));
        Assert.Equal("old-b", Read(area.Path, "b.dll"));
        Assert.Equal(new[] { "a.dll", "b.dll" },
            Directory.GetFiles(area.Path).Select(Path.GetFileName).OrderBy(n => n).ToArray());
        Assert.Equal(1, restored);
        Assert.Equal(1, removed);
    }

    [Fact]
    public void ОригиналПереименованНоНовыйНеПоложен_ВозвращаетсяОригинал()
    {
        using var area = new TemporaryDirectory();
        Write(area.Path, $"a.dll.old-{Op}", "old-a");
        Write(area.Path, $"a.dll.new-{Op}", "new-a");

        TransactionalDirectoryInstaller.RecoverLeftovers(area.Path);

        Assert.Equal("old-a", Read(area.Path, "a.dll"));
        Assert.Single(Directory.GetFiles(area.Path));
    }

    [Fact]
    public void ЗавершённаяФиксация_НовыеФайлыОстаются_КопииУдаляются()
    {
        using var area = new TemporaryDirectory();
        Write(area.Path, "a.dll", "new-a");
        Write(area.Path, $"a.dll.old-{Op}", "old-a");

        var (restored, removed) = TransactionalDirectoryInstaller.RecoverLeftovers(area.Path);

        Assert.Equal("new-a", Read(area.Path, "a.dll"));
        Assert.Single(Directory.GetFiles(area.Path));
        Assert.Equal(0, restored);
        Assert.Equal(1, removed);
    }

    [Fact]
    public void ЗаготовкаДругойОперации_НеОткатываетЗавершённую()
    {
        using var area = new TemporaryDirectory();
        const string other = "fedcba9876543210fedcba9876543210";
        Write(area.Path, "a.dll", "new-a");
        Write(area.Path, $"a.dll.old-{Op}", "old-a");
        Write(area.Path, $"c.dll.new-{other}", "stray");

        TransactionalDirectoryInstaller.RecoverLeftovers(area.Path);

        Assert.Equal("new-a", Read(area.Path, "a.dll"));
        Assert.Single(Directory.GetFiles(area.Path));
    }

    [Theory]
    [InlineData(".Ven4Tools_Client.staging-0123456789abcdef0123456789abcdef", true, false)]
    [InlineData("Ven4Tools_Client.backup-0123456789abcdef0123456789abcdef", true, true)]
    [InlineData("Ven4Tools_Client.backup-old", false, true)]
    [InlineData("Ven4Tools_Client.backup-0123456789abcdef0123456789abcdef-копия", false, true)]
    [InlineData(".Ven4Tools_Client.staging-", false, false)]
    [InlineData("Ven4Tools_Client", false, false)]
    public void КаталогиОстатков_ОпознаютсяТолькоПоСтрогомуШаблону(string name, bool expected, bool expectedBackup)
    {
        bool matched = TransactionalDirectoryInstaller.IsInstallLeftoverDirectory(name, "Ven4Tools_Client", out bool isBackup);

        Assert.Equal(expected, matched);
        if (matched) Assert.Equal(expectedBackup, isBackup);
    }
}
