using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Tests;

public sealed class ClientShortcutCleanerTests
{
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

        ClientShortcutCleaner.Clean(new[] { desktop }, new[] { programs });

        Assert.True(File.Exists(launcherDesktop));
        Assert.True(File.Exists(launcherStart));
        Assert.True(File.Exists(uninstallStart));
        Assert.False(File.Exists(clientDesktop));
        Assert.False(File.Exists(clientStart));
        Assert.True(Directory.Exists(ven4Tools));
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
