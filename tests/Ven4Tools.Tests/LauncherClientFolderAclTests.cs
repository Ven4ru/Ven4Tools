using System.Security.AccessControl;
using System.Security.Principal;
using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Tests;

/// <summary>
/// Проверка прав на папку установленного клиента («Проверить и восстановить клиент»).
///
/// Живой прогон 2026-09-10: предупреждение «файлы может изменить любой пользователь
/// этого компьютера» показывалось ВСЕГДА. Строгая проверка
/// <see cref="TrustedExecutablePaths.IsDirectoryAclCompromised"/> написана для системных
/// каталогов (пакет winget, chocolatey\bin), где право записи у обычного пользователя —
/// признак подмены. Клиент же всегда стоит внутри профиля пользователя, где его
/// собственный SID имеет FullControl по определению, и та же проверка объявляла
/// скомпрометированной любую нормальную установку. Постоянно горящее предупреждение
/// перестаёт что-либо значить ровно тогда, когда права ослаблены по-настоящему.
/// </summary>
public sealed class LauncherClientFolderAclTests
{
    private static SecurityIdentifier CurrentUser => WindowsIdentity.GetCurrent().User!;

    private static void ApplyAcl(string path, params (SecurityIdentifier Sid, FileSystemRights Rights)[] rules)
    {
        var security = new DirectorySecurity();
        // Наследование отключаем: тест должен видеть ровно те правила, что задал,
        // а не то, что досталось от временного каталога машины.
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var (sid, rights) in rules)
        {
            security.AddAccessRule(new FileSystemAccessRule(
                sid, rights,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        }
        new DirectoryInfo(path).SetAccessControl(security);
    }

    [Fact]
    public void TypicalUserFolder_IsNotReportedAsWritableByOthers()
    {
        using var dir = new TemporaryDirectory();
        ApplyAcl(dir.Path,
            (CurrentUser, FileSystemRights.FullControl),
            (new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl),
            (new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl));

        TrustedExecutablePaths.InvalidateAclCache(dir.Path);

        Assert.False(TrustedExecutablePaths.IsDirectoryWritableByOtherUsers(dir.Path));
    }

    [Fact]
    public void FolderWritableByEveryone_IsReported()
    {
        using var dir = new TemporaryDirectory();
        ApplyAcl(dir.Path,
            (CurrentUser, FileSystemRights.FullControl),
            (new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Modify));

        TrustedExecutablePaths.InvalidateAclCache(dir.Path);

        Assert.True(TrustedExecutablePaths.IsDirectoryWritableByOtherUsers(dir.Path));
    }

    /// <summary>
    /// Строгая проверка своё поведение не меняет: для системных каталогов запись
    /// самим пользователем по-прежнему повод отказаться от найденного winget/choco.
    /// </summary>
    [Fact]
    public void StrictCheckStillFlagsCurrentUserWriteAccess()
    {
        using var dir = new TemporaryDirectory();
        ApplyAcl(dir.Path,
            (CurrentUser, FileSystemRights.FullControl),
            (new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl));

        TrustedExecutablePaths.InvalidateAclCache(dir.Path);

        Assert.True(TrustedExecutablePaths.IsDirectoryAclCompromised(dir.Path));
    }

    [Fact]
    public void InvalidateAclCache_ClearsBothVerdicts()
    {
        using var dir = new TemporaryDirectory();

        _ = TrustedExecutablePaths.IsDirectoryAclCompromised(dir.Path);
        _ = TrustedExecutablePaths.IsDirectoryWritableByOtherUsers(dir.Path);
        Assert.True(TrustedExecutablePaths.IsAclCacheEntryCached(dir.Path));

        TrustedExecutablePaths.InvalidateAclCache(dir.Path);

        Assert.False(TrustedExecutablePaths.IsAclCacheEntryCached(dir.Path));
    }
}
