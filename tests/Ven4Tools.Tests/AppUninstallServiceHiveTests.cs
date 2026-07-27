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
    public void НесуществующееПриложение_НеНаходится()
    {
        var found = AppUninstallService.FindUninstallString(
            "Ven4Tools заведомо отсутствующее приложение " + Guid.NewGuid().ToString("N"));

        Assert.Null(found);
    }
}
