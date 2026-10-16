using Microsoft.Win32;
using Ven4Tools.Services;

namespace Ven4Tools.Tests;

/// <summary>
/// Регрессионные тесты на источник команды деинсталляции.
///
/// Предыстория: HKLM-only-поиск был введён аудитом безопасности как защита от
/// связки «непривилегированные данные → elevated-действие» (клиент работает с
/// правами администратора, дочерний процесс наследует его токен). Позже HKCU
/// вернули обратно как UX-фикс для user-scope установок, не заметив, что этим
/// снимается защита. Эти тесты фиксируют, что источник записи различается явно,
/// чтобы откат защиты не прошёл молча ещё раз.
/// </summary>
public sealed class AppUninstallServiceHiveTests
{
    private const string UninstallKeyPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    [Fact]
    public void ЗаписьИзHKCU_ПомечаетсяКакНедоверенная()
    {
        string appName = "Ven4Tools тестовая запись " + Guid.NewGuid().ToString("N");
        string subKey  = "Ven4ToolsTest_" + Guid.NewGuid().ToString("N");

        using (var root = Registry.CurrentUser.CreateSubKey(UninstallKeyPath + "\\" + subKey))
        {
            Assert.NotNull(root);
            root!.SetValue("DisplayName", appName);
            root.SetValue("UninstallString", @"C:\Windows\System32\cmd.exe /c rem");
        }

        try
        {
            var found = AppUninstallService.FindUninstallString(appName);

            Assert.NotNull(found);
            // Главное утверждение: запись найдена, но источник — пользовательский
            // куст, поэтому исполнять её с повышенными правами нельзя.
            Assert.False(found!.Value.FromMachineHive);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKeyPath + "\\" + subKey, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void ОбрезанноеИмя_ЕдинственноеСовпадение_Находится()
    {
        // winget list обрезает длинные имена многоточием («Adobe Acrobat Reader…»).
        string unique = Guid.NewGuid().ToString("N");
        string fullName = "Ven4Tools обрезка " + unique + " (64-bit) длинное имя";
        string subKey = "Ven4ToolsTest_" + Guid.NewGuid().ToString("N");
        using (var root = Registry.CurrentUser.CreateSubKey(UninstallKeyPath + "\\" + subKey))
        {
            root!.SetValue("DisplayName", fullName);
            root.SetValue("UninstallString", @"C:\Windows\System32\cmd.exe /c rem");
        }
        try
        {
            var found = AppUninstallService.FindUninstallString("Ven4Tools обрезка " + unique + " (64…");
            Assert.NotNull(found);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKeyPath + "\\" + subKey, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void ОбрезанноеИмя_НесколькоСовпадений_НеНаходится()
    {
        string prefix = "Ven4Tools двойник " + Guid.NewGuid().ToString("N");
        string a = "Ven4ToolsTest_" + Guid.NewGuid().ToString("N");
        string b = "Ven4ToolsTest_" + Guid.NewGuid().ToString("N");
        foreach (var (key, suffix) in new[] { (a, " x64"), (b, " x86") })
        {
            using var root = Registry.CurrentUser.CreateSubKey(UninstallKeyPath + "\\" + key);
            root!.SetValue("DisplayName", prefix + suffix);
            root.SetValue("UninstallString", @"C:\Windows\System32\cmd.exe /c rem");
        }
        try
        {
            // Неоднозначное начало имени — удалять наугад нельзя.
            Assert.Null(AppUninstallService.FindUninstallString(prefix + "…"));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKeyPath + "\\" + a, throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKeyPath + "\\" + b, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void НесуществующееПриложение_НеНаходится()
    {
        var found = AppUninstallService.FindUninstallString(
            "Ven4Tools заведомо отсутствующее приложение " + Guid.NewGuid().ToString("N"));

        Assert.Null(found);
    }
}
