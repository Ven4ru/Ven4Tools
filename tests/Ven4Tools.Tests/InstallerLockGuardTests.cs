using System;
using System.Linq;
using Ven4Tools.Services;

namespace Ven4Tools.Tests;

public sealed class InstallerLockGuardTests
{
    private static readonly DateTime T0 = new(2026, 10, 17, 10, 35, 0, DateTimeKind.Utc);

    // Командная строка окна доверия драйверу RaiDrive со стенда 17.10.2026.
    private const string PromptCmd =
        @"rundll32.exe C:\WINDOWS\system32\pnpui.dll,InstallSecurityPromptRunDllW 20 Global\{766202FF-90EE-48DA-B98C-B6DACC652F3D} Global\{DA498C43-7E3A-4899-A289-B2107C27BB0C} C:\WINDOWS\System32\DriverStore\Temp\{7812c115-b7dd-0044-a38a-ee472a69571d}\vpnpbus.inf C:\WINDOWS\System32\DriverStore\Temp\{7812c115-b7dd-0044-a38a-ee472a69571d}\vpnpbus.cat";

    private static InstallerLockGuard.InstallerProcess P(int pid, string name, string? cmd, int minute) =>
        new(pid, name, cmd, T0.AddMinutes(minute));

    [Fact]
    public void IsDriverPromptCommandLine_ОкноДоверияДрайверу_Распознаётся()
    {
        Assert.True(InstallerLockGuard.IsDriverPromptCommandLine(PromptCmd));
        Assert.False(InstallerLockGuard.IsDriverPromptCommandLine(@"rundll32.exe C:\WINDOWS\system32\shell32.dll,Control_RunDLL"));
        Assert.False(InstallerLockGuard.IsDriverPromptCommandLine(null));
    }

    [Fact]
    public void ExtractInfPath_БерётПервыйInf()
    {
        Assert.Equal(
            @"C:\WINDOWS\System32\DriverStore\Temp\{7812c115-b7dd-0044-a38a-ee472a69571d}\vpnpbus.inf",
            InstallerLockGuard.ExtractInfPath(PromptCmd));
        Assert.Null(InstallerLockGuard.ExtractInfPath("rundll32.exe pnpui.dll,InstallSecurityPromptRunDllW 20"));
    }

    [Fact]
    public void ParseInfProvider_СсылкаНаStrings_Разворачивается()
    {
        const string inf = "[Version]\r\nSignature=\"$WINDOWS NT$\"\r\nProvider=%Mfg% ; издатель\r\n\r\n[Strings]\r\nMfg=\"EldoS Corporation\"\r\n";
        Assert.Equal("EldoS Corporation", InstallerLockGuard.ParseInfProvider(inf));
    }

    [Fact]
    public void ParseInfProvider_ПрямоеЗначениеИлиПусто()
    {
        Assert.Equal("Acme", InstallerLockGuard.ParseInfProvider("[Version]\nProvider = Acme\n"));
        Assert.Null(InstallerLockGuard.ParseInfProvider("[Version]\nClass=Net\n"));
        Assert.Null(InstallerLockGuard.ParseInfProvider("[Version]\nProvider=%Missing%\n"));
        Assert.Null(InstallerLockGuard.ParseInfProvider("[Other]\nProvider=Acme\n"));
    }

    [Fact]
    public void SelectLeftovers_ТолькоОстаткиНачатыеПослеПопытки()
    {
        var all = new[]
        {
            P(1, "msiexec.exe", @"C:\WINDOWS\system32\msiexec.exe /V", 1),
            P(2, "msiexec.exe", @"C:\Windows\syswow64\MsiExec.exe -Embedding 289827C3231F8EB492BAB321FD4CE632 E Global\MSI0000", 1),
            P(3, "msiexec.exe", @"C:\Windows\syswow64\MsiExec.exe -Embedding 86D06BFEFB0B91A2608CF4CCFEF2B1FB", -5),
            P(4, "DrvInst.exe", @"DrvInst.exe ""4"" ""0"" vpnpbus.inf", 1),
            P(5, "rundll32.exe", PromptCmd, 1),
            P(6, "rundll32.exe", @"rundll32.exe shell32.dll,Control_RunDLL", 1),
        };

        var picked = InstallerLockGuard.SelectLeftovers(all, T0).Select(p => p.Pid).ToArray();

        // Служба /V, чужой более старый msiexec и посторонний rundll32 не трогаются.
        Assert.Equal(new[] { 2, 4, 5 }, picked);
    }

    [Fact]
    public void IsMsiBusyFailure_1618ИУстановкаУжеИдётУWinget()
    {
        Assert.True(InstallerLockGuard.IsMsiBusyFailure(
            "Не установлено: все источники исчерпаны — Winget: " + MsiExitCodes.Known[1618] + " · Chocolatey: не пробовался · Прямая ссылка: не пробовался"));
        Assert.True(InstallerLockGuard.IsMsiBusyFailure(WingetErrorMapper.MapExitCode(unchecked((int)0x8A150102))));
        Assert.False(InstallerLockGuard.IsMsiBusyFailure("Не установлено: Пакет не найден в источнике или недоступен для этой системы."));
        Assert.False(InstallerLockGuard.IsMsiBusyFailure(null));
    }
}
