using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Tests;

public sealed class InstallPathGuardTests
{
    [Theory]
    [InlineData(@"C:\Ven4Tools\Ven4Tools_Client", @"C:\Users\test\AppData\Local\Ven4Tools", true)]
    [InlineData(@"C:\Users\test\AppData\Local\Ven4Tools", @"C:\Users\test\AppData\Local\Ven4Tools", false)]
    [InlineData(@"C:\Users\test\AppData\Local\Ven4Tools\Client", @"C:\Users\test\AppData\Local\Ven4Tools", false)]
    [InlineData(@"C:\Users\test\AppData\Local\Ven4Tools", @"C:\Users\test\AppData\Local\Ven4Tools\Client", false)]
    [InlineData(@"C:\Users\test\AppData\Local\ven4tools", @"C:\Users\test\AppData\Local\Ven4Tools", false)]
    [InlineData(@"C:\Users\test\AppData\Local\Ven4ToolsExtra", @"C:\Users\test\AppData\Local\Ven4Tools", true)]
    public void IsClientPathSafe_DetectsOverlapWithDataFolder(string clientPath, string dataFolderPath, bool expectedSafe)
    {
        Assert.Equal(expectedSafe, InstallPathGuard.IsClientPathSafe(clientPath, dataFolderPath));
    }

    // Сценарий аудита 2026-07-13: BtnFindClient_Click находит Ven4Tools.exe прямо
    // в корне известной пользовательской папки (Downloads, распакованный туда
    // архив без подпапки) и присваивает _clientPath = сам этот корень.
    // TransactionalDirectoryInstaller/удаление клиента затем уничтожают его
    // реальное содержимое. Данные фикции не пересекаются, поэтому единственная
    // причина отказа — именно защита известных корней, не старая проверка
    // пересечения с папкой данных.
    [Theory]
    [InlineData(Environment.SpecialFolder.MyDocuments)]
    [InlineData(Environment.SpecialFolder.Desktop)]
    [InlineData(Environment.SpecialFolder.ProgramFiles)]
    [InlineData(Environment.SpecialFolder.ProgramFilesX86)]
    [InlineData(Environment.SpecialFolder.UserProfile)]
    public void IsClientPathSafe_RejectsKnownUserContentRootItself(Environment.SpecialFolder root)
    {
        string clientPath = Environment.GetFolderPath(root);
        string unrelatedDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ven4Tools");

        Assert.False(InstallPathGuard.IsClientPathSafe(clientPath, unrelatedDataFolder));
    }

    [Fact]
    public void IsClientPathSafe_AllowsSubfolderInsideKnownUserContentRoot()
    {
        string clientPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Ven4Tools_Client");
        string unrelatedDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ven4Tools");

        Assert.True(InstallPathGuard.IsClientPathSafe(clientPath, unrelatedDataFolder));
    }
}
