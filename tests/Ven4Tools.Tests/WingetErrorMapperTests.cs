using Ven4Tools.Services;

namespace Ven4Tools.Tests;

public sealed class WingetErrorMapperTests
{
    [Fact]
    public void MapExitCode_КодWindowsInstaller1618_СообщаетОПараллельнойУстановке()
    {
        var message = WingetErrorMapper.MapExitCode(1618);
        Assert.Contains("уже выполняется", message);
    }

    [Fact]
    public void MapExitCode_КодHresultСети_СообщаетОбОшибкеСети()
    {
        var message = WingetErrorMapper.MapExitCode(unchecked((int)0x80072EE2));
        Assert.Contains("сети", message);
    }

    [Fact]
    public void MapExitCode_КодНесовпаденияХеша_СообщаетОХеше()
    {
        // APPINSTALLER_CLI_ERROR_INSTALLER_HASH_MISMATCH.
        var message = WingetErrorMapper.MapExitCode(unchecked((int)0x8A150011));
        Assert.Contains("Хеш", message);
    }

    [Fact]
    public void MapExitCode_ПакетТребуетПапку_СообщаетОПапкеУстановки()
    {
        // APPINSTALLER_CLI_ERROR_INSTALL_LOCATION_REQUIRED (Battle.net без --location).
        Assert.Equal(unchecked((int)0x8A15005F), WingetErrorMapper.InstallLocationRequired);
        var message = WingetErrorMapper.MapExitCode(WingetErrorMapper.InstallLocationRequired);
        Assert.Contains("папку установки", message);
    }

    [Theory]
    [InlineData(3010)]
    [InlineData(unchecked((int)0x8A150109))]
    [InlineData(unchecked((int)0x8A15010B))]
    public void IsSuccessWithReboot_КодыПерезагрузки(int code)
    {
        Assert.True(WingetErrorMapper.IsSuccessWithReboot(code));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(unchecked((int)0x8A15002C))]
    [InlineData(unchecked((int)0x8A15010A))]
    public void IsSuccessWithReboot_НеКодыПерезагрузки(int code)
    {
        Assert.False(WingetErrorMapper.IsSuccessWithReboot(code));
    }

    [Fact]
    public void MapExitCode_КодПерезагрузкиДляЗавершения_НеВыдаётсяЗаОшибкуХеша()
    {
        // APPINSTALLER_CLI_ERROR_INSTALL_REBOOT_REQUIRED_TO_FINISH — раньше ошибочно
        // подписывался как несовпадение хеша.
        var message = WingetErrorMapper.MapExitCode(unchecked((int)0x8A150109));
        Assert.Contains("перезагрузка", message);
        Assert.DoesNotContain("Хеш", message);
    }

    [Fact]
    public void MapExitCode_НеизвестныйКод_СодержитЧислоИHex()
    {
        var message = WingetErrorMapper.MapExitCode(unchecked((int)0x12345678));
        Assert.Contains("12345678", message);
        Assert.Contains("305419896", message);
    }

    [Theory]
    [InlineData(unchecked((int)0x8A15010D))] // INSTALL_ALREADY_INSTALLED
    [InlineData(unchecked((int)0x8A15010E))] // INSTALL_DOWNGRADE
    [InlineData(unchecked((int)0x8A15004F))] // UPGRADE_VERSION_NOT_NEWER
    public void ClassifyInstallFailure_УжеУстановлено(int code)
    {
        Assert.Equal(WingetFailureAction.AlreadyInstalled, WingetErrorMapper.ClassifyInstallFailure(code));
    }

    [Theory]
    [InlineData(unchecked((int)0x8A150101))] // INSTALL_PACKAGE_IN_USE
    [InlineData(unchecked((int)0x8A150103))] // INSTALL_FILE_IN_USE
    [InlineData(unchecked((int)0x8A150111))] // INSTALL_PACKAGE_IN_USE_BY_APPLICATION
    [InlineData(unchecked((int)0x8A150105))] // INSTALL_DISK_FULL
    [InlineData(unchecked((int)0x8A15010C))] // INSTALL_CANCELLED_BY_USER
    public void ClassifyInstallFailure_ДругиеИсточникиНеПомогут_Останавливает(int code)
    {
        Assert.Equal(WingetFailureAction.Stop, WingetErrorMapper.ClassifyInstallFailure(code));
    }

    [Theory]
    [InlineData(1603)]
    [InlineData(unchecked((int)0x8A150014))] // пакет не найден — choco может знать его
    [InlineData(unchecked((int)0x8A150011))] // хеш не совпал — у прямой ссылки свой файл
    [InlineData(unchecked((int)0x8A150102))] // другая установка — решает общий семафор
    [InlineData(WingetErrorMapper.StalledByClient)]
    public void ClassifyInstallFailure_ОстальноеИдётКСледующемуИсточнику(int code)
    {
        Assert.Equal(WingetFailureAction.NextSource, WingetErrorMapper.ClassifyInstallFailure(code));
    }

    [Fact]
    public void MapExitCode_ПрограммаЗапущена_ПроситЗакрыть()
    {
        Assert.Contains("закройте", WingetErrorMapper.MapExitCode(unchecked((int)0x8A150101)));
    }

    [Fact]
    public void КодыОбластиИПрав_СовпадаютСоСпискомWinget()
    {
        // doc/windows/package-manager/winget/returnCodes.md в winget-cli.
        Assert.Equal(unchecked((int)0x8A150010), WingetErrorMapper.NoApplicableInstaller);
        Assert.Equal(unchecked((int)0x8A150056), WingetErrorMapper.InstallerProhibitsElevation);
    }

    // Стенд 18.10.2026: с --scope machine Notion, Slack, Claude отвечали 0x8A150113,
    // GitHub Copilot CLI — 0x8A150110 (его зависимость PowerShell — MSIX); без --scope встают.
    [Theory]
    [InlineData(unchecked((int)0x8A150010))]
    [InlineData(unchecked((int)0x8A150113))]
    [InlineData(unchecked((int)0x8A150110))]
    public void ОтказОбластиДляВсех_ПовторБезScope(int code)
        => Assert.True(WingetErrorMapper.ShouldRetryWithoutScope(code));

    [Theory]
    [InlineData(0)]
    [InlineData(unchecked((int)0x8A150014))]
    [InlineData(unchecked((int)0x8A150101))]
    [InlineData(unchecked((int)0x80072EE2))]
    public void ПрочиеКоды_БезПовтораБезScope(int code)
        => Assert.False(WingetErrorMapper.ShouldRetryWithoutScope(code));

    [Fact]
    public void СистемаНеПоддерживает_ПонятныйТекст()
        => Assert.DoesNotContain("0x", WingetErrorMapper.MapExitCode(unchecked((int)0x8A150113)));
}
