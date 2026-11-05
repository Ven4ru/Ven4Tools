using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Tests;

public sealed class ClientShortcutCleanerTests
{
    private const string ClientPath = @"C:\Users\Тест\AppData\Local\Programs\Ven4Tools\Ven4Tools_Client";
    private const string ClientExe = ClientPath + @"\Ven4Tools.exe";

    [Fact]
    public void Clean_УдаляетТолькоЯрлыкиКлиента()
    {
        using var root = new TemporaryDirectory();
        string desktop = Path.Combine(root.Path, "Рабочий стол");
        string programs = Path.Combine(root.Path, "Меню Пуск");
        string ven4Tools = Path.Combine(programs, "Ven4Tools");
        Directory.CreateDirectory(desktop);
        Directory.CreateDirectory(ven4Tools);

        string launcherDesktop = CreateShortcut(desktop, "Ven4Tools Launcher.lnk");
        string clientDesktop = CreateShortcut(desktop, "Ven4Tools Client.lnk");
        string launcherStart = CreateShortcut(ven4Tools, "Ven4Tools Launcher.lnk");
        string uninstallStart = CreateShortcut(ven4Tools, "Удалить Ven4Tools Launcher.lnk");
        string clientStart = CreateShortcut(ven4Tools, "Ven4Tools.lnk");

        var kept = ClientShortcutCleaner.Clean(new[] { desktop }, new[] { programs }, ClientPath, _ => ClientExe);

        Assert.Empty(kept);
        Assert.True(File.Exists(launcherDesktop));
        Assert.True(File.Exists(launcherStart));
        Assert.True(File.Exists(uninstallStart));
        Assert.False(File.Exists(clientDesktop));
        Assert.False(File.Exists(clientStart));
        Assert.True(Directory.Exists(ven4Tools));
    }

    [Fact]
    public void Clean_ЯрлыкНаЧужуюПрограмму_Оставляется()
    {
        using var root = new TemporaryDirectory();
        string desktop = Path.Combine(root.Path, "Рабочий стол");
        Directory.CreateDirectory(desktop);
        string foreign = CreateShortcut(desktop, "Ven4Tools.lnk");
        string own = CreateShortcut(desktop, "Ven4Tools Client.lnk");

        var targets = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [foreign] = @"D:\Другие программы\Ven4Tools\Ven4Tools.exe",
            [own] = ClientExe,
        };

        var kept = ClientShortcutCleaner.Clean(new[] { desktop }, Array.Empty<string>(), ClientPath, p => targets[p]);

        Assert.Equal(new[] { foreign }, kept);
        Assert.True(File.Exists(foreign));
        Assert.False(File.Exists(own));
    }

    [Fact]
    public void Clean_ЦельНеЧитается_ЯрлыкОставляется()
    {
        using var root = new TemporaryDirectory();
        string desktop = Path.Combine(root.Path, "Рабочий стол");
        Directory.CreateDirectory(desktop);
        string unreadable = CreateShortcut(desktop, "Ven4Tools.lnk");
        string throwing = CreateShortcut(desktop, "Ven4Tools Client.lnk");

        var kept = ClientShortcutCleaner.Clean(new[] { desktop }, Array.Empty<string>(), ClientPath,
            p => p == throwing ? throw new InvalidOperationException("оболочка недоступна") : null);

        Assert.Equal(2, kept.Count);
        Assert.True(File.Exists(unreadable));
        Assert.True(File.Exists(throwing));
    }

    [Theory]
    [InlineData(ClientExe, true)]
    [InlineData(ClientPath, true)]
    [InlineData(ClientPath + @"\Personalization\Venchik.scr", true)]
    [InlineData(ClientPath + @"_old\Ven4Tools.exe", false)]
    [InlineData(@"C:\Users\Тест\AppData\Local\Programs\Ven4Tools\Ven4Tools.exe", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void PointsIntoClientFolder(string? target, bool expected)
    {
        Assert.Equal(expected, ClientShortcutCleaner.PointsIntoClientFolder(target, ClientPath));
    }

    [Fact]
    public void ИменаЯрлыковКлиента_НеСодержатЯрлыкLauncher()
    {
        Assert.DoesNotContain(
            "Ven4Tools Launcher.lnk",
            ClientShortcutCleaner.ClientShortcutNames);
    }

    private static string CreateShortcut(string directory, string name)
    {
        string path = Path.Combine(directory, name);
        File.WriteAllText(path, "тест");
        return path;
    }
}
