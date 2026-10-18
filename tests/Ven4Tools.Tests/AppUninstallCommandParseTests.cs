using System;
using System.IO;
using Ven4Tools.Services;

namespace Ven4Tools.Tests;

public sealed class AppUninstallCommandParseTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "v4t-unins-" + Guid.NewGuid().ToString("N"));

    public AppUninstallCommandParseTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Touch(string name)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, "");
        return path;
    }

    [Fact]
    public void Inno_ПолучаетVerysilent()
    {
        string exe = Touch("unins000.exe");
        var (file, args, error) = AppUninstallService.ParseUninstallCommand($"\"{exe}\"");
        Assert.Null(error);
        Assert.Equal(exe, file);
        Assert.Contains("/VERYSILENT", args);
        Assert.DoesNotContain("/S ", args + " ");
    }

    [Fact]
    public void Nsis_ПолучаетS()
    {
        string exe = Touch("uninstall.exe");
        var (_, args, error) = AppUninstallService.ParseUninstallCommand($"\"{exe}\"");
        Assert.Null(error);
        Assert.StartsWith("/S", args);
    }

    [Fact]
    public void ТихаяКомандаПрограммы_НеМеняется()
    {
        string exe = Touch("Update.exe");
        var (_, args, error) = AppUninstallService.ParseUninstallCommand($"\"{exe}\" --uninstall -s", quiet: true);
        Assert.Null(error);
        Assert.Equal("--uninstall -s", args);
    }

    [Fact]
    public void Msi_ТихоеУдалениеПоКодуПродукта()
    {
        var (file, args, error) = AppUninstallService.ParseUninstallCommand("MsiExec.exe /I{AC76BA86-1033-FF00-7760-BC15014EA700}");
        Assert.Null(error);
        Assert.EndsWith("msiexec.exe", file, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("/x {AC76BA86-1033-FF00-7760-BC15014EA700} /quiet /norestart", args);
    }

    [Theory]
    [InlineData("unins000.exe", true)]
    [InlineData("UNINS012.EXE", true)]
    [InlineData("uninstall.exe", false)]
    [InlineData("unins.exe", false)]
    public void IsInnoUninstaller(string name, bool expected)
    {
        Assert.Equal(expected, AppUninstallService.IsInnoUninstaller(Path.Combine(@"C:\Program Files\X", name)));
    }

    [Theory]
    [InlineData("AIMP Setup", "Artem Izmaylov", true)]
    [InlineData("aimp setup ", "Artem Izmaylov", true)]
    [InlineData("AIMP Setup", "Other Corp", false)]
    [InlineData("AIMP", "Artem Izmaylov", false)]
    [InlineData(null, null, false)]
    public void IsAimpUninstaller(string? description, string? company, bool expected)
    {
        Assert.Equal(expected, AppUninstallService.IsAimpUninstaller(description, company));
    }

    [Fact]
    public void AnyDesk_ПолучаетRemoveВместоUninstall()
    {
        // --uninstall открывает окно подтверждения и ничего не удаляет (стенд 17.10.2026).
        string exe = Touch("AnyDesk.exe");
        var (file, args, error) = AppUninstallService.ParseUninstallCommand($"\"{exe}\" --uninstall");
        Assert.Null(error);
        Assert.Equal(exe, file);
        Assert.Equal("--remove", args);
    }

    [Fact]
    public void DockerDesktop_ПолучаетUninstallQuietВместоS()
    {
        // С добавленным /S установщик Docker завершался с кодом -5 (стенд 17.10.2026).
        string exe = Touch("Docker Desktop Installer.exe");
        var (file, args, error) = AppUninstallService.ParseUninstallCommand($"\"{exe}\" uninstall");
        Assert.Null(error);
        Assert.Equal(exe, file);
        Assert.Equal("uninstall --quiet", args);
    }

    [Fact]
    public void AnyDesk_ГотоваяТихаяКомандаНеТрогается()
    {
        string exe = Touch("AnyDesk.exe");
        var (_, args, _) = AppUninstallService.ParseUninstallCommand($"\"{exe}\" --silent --uninstall", quiet: true);
        Assert.Equal("--silent --uninstall", args);
    }

    [Fact]
    public void ПустойФайл_НеСчитаетсяДеинсталляторомAimp()
    {
        // У файла без свойств версии — обычный /S, а не ключи AIMP.
        string exe = Touch("Uninstall.exe");
        var (_, args, error) = AppUninstallService.ParseUninstallCommand($"\"{exe}\"");
        Assert.Null(error);
        Assert.DoesNotContain("/AUTO", args);
    }

    private string WriteNsisLike(string name)
    {
        string path = Path.Combine(_dir, name);
        var head = new byte[4096];
        System.Text.Encoding.ASCII.GetBytes("<assemblyIdentity name=\"Nullsoft.NSIS.exehead\"/>").CopyTo(head, 1024);
        File.WriteAllBytes(path, head);
        return path;
    }

    [Fact]
    public void Nsis_ОпределяетсяПоМанифесту()
    {
        Assert.True(AppUninstallService.IsNsisUninstaller(WriteNsisLike("uninst.exe")));
        Assert.False(AppUninstallService.IsNsisUninstaller(Touch("Uninstall.exe")));
        Assert.False(AppUninstallService.IsNsisUninstaller(Path.Combine(_dir, "нет.exe")));
    }

    // FastStone, FileZilla (стенд 18.10.2026): winget запускал их без /S, и окно ждало ответа.
    [Fact]
    public void NsisИзHklmБезТихойКоманды_УдаляетсяМимоWinget()
        => Assert.True(AppUninstallService.PreferDirectUninstall(fromMachineHive: true, quiet: false, WriteNsisLike("uninst.exe")));

    [Fact]
    public void Inno_УдаляетсяМимоWinget()
        => Assert.True(AppUninstallService.PreferDirectUninstall(true, false, Touch("unins000.exe")));

    [Fact]
    public void НеизвестныйExe_ОстаётсяЗаWinget()
        => Assert.False(AppUninstallService.PreferDirectUninstall(true, false, Touch("setup.exe")));

    [Fact]
    public void ГотоваяТихаяКомандаИлиHkcu_ОстаютсяЗаWinget()
    {
        string nsis = WriteNsisLike("uninstall.exe");
        Assert.False(AppUninstallService.PreferDirectUninstall(true, quiet: true, nsis));
        Assert.False(AppUninstallService.PreferDirectUninstall(fromMachineHive: false, false, nsis));
    }

    // Greenshot, MPC-BE (стенд 18.10.2026): готовая тихая команда «unins000.exe /SILENT»
    // всё равно открывала вопрос из сценария удаления.
    [Fact]
    public void InnoСГотовойТихойКомандой_ПолучаетVerysilentИУдаляетсяМимоWinget()
    {
        string exe = Touch("unins000.exe");
        var (_, args, error) = AppUninstallService.ParseUninstallCommand($"\"{exe}\" /SILENT", quiet: true);
        Assert.Null(error);
        Assert.Equal("/VERYSILENT /SUPPRESSMSGBOXES /NORESTART", args);
        Assert.True(AppUninstallService.PreferDirectUninstall(true, quiet: true, exe));
    }

    [Theory]
    [InlineData("/SILENT", "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART")]
    [InlineData("", "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART")]
    [InlineData("/silent /LOG=\"C:\\x.log\"", "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG=\"C:\\x.log\"")]
    [InlineData("/VERYSILENT /NORESTART", "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART")]
    public void InnoVerySilentArgs_БезДублейИСохраняетПрочее(string input, string expected)
        => Assert.Equal(expected, AppUninstallService.InnoVerySilentArgs(input));

    // Chrome, Brave, Opera (стенд 18.10.2026): без этих ключей деинсталлятор спрашивает «Удалить?».
    [Theory]
    [InlineData(@"C:\Program Files\Google\Chrome\Application\150.0\Installer\setup.exe", "--uninstall --channel=stable --system-level --verbose-logging", "--uninstall --channel=stable --system-level --verbose-logging --force-uninstall")]
    [InlineData(@"C:\Program Files\Opera\launcher.exe", "--uninstall", "--uninstall --runimmediately --deleteuserprofile=0")]
    [InlineData(@"C:\Program Files\Opera\launcher.exe", "--uninstall --runimmediately", "--uninstall --runimmediately --deleteuserprofile=0")]
    [InlineData(@"C:\Program Files\Opera GX\opera.exe", "/uninstall", "--uninstall --runimmediately --deleteuserprofile=0")]
    public void Браузеры_ПолучаютКлючиТихогоУдаления(string exe, string args, string expected)
        => Assert.Equal(expected, AppUninstallService.BrowserSilentUninstallArgs(exe, args));

    [Theory]
    [InlineData(@"C:\Program Files\App\setup.exe", "/remove")]
    [InlineData(@"C:\Program Files\Game\launcher.exe", "--uninstall")]
    public void НеБраузеры_БезКлючейБраузера(string exe, string args)
        => Assert.Null(AppUninstallService.BrowserSilentUninstallArgs(exe, args));

    [Fact]
    public void Chromium_УдаляетсяМимоWinget()
        => Assert.True(AppUninstallService.PreferDirectUninstall(true, false, Touch("setup.exe"), "--uninstall --system-level"));

    [Theory]
    [InlineData(@"C:\Program Files\Greenshot\Greenshot.exe", @"C:\Program Files\Greenshot", true)]
    [InlineData(@"C:\Program Files\Greenshot\bin\x.exe", @"C:\Program Files\Greenshot\", true)]
    [InlineData(@"C:\Program Files\GreenshotPlus\x.exe", @"C:\Program Files\Greenshot", false)]
    [InlineData(null, @"C:\Program Files\Greenshot", false)]
    public void ПроцессВПапкеПрограммы(string? file, string dir, bool expected)
        => Assert.Equal(expected, AppUninstallService.IsUnderDir(file, dir));

    // Перед удалением закрываются процессы из папки программы — общие папки для этого не годятся.
    [Fact]
    public void ОбщиеПапки_НеСчитаютсяПапкойПрограммы()
    {
        Assert.False(AppUninstallService.IsProgramOwnDir(null));
        Assert.False(AppUninstallService.IsProgramOwnDir(Path.GetPathRoot(_dir)));
        Assert.False(AppUninstallService.IsProgramOwnDir(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)));
        Assert.False(AppUninstallService.IsProgramOwnDir(Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
        Assert.False(AppUninstallService.IsProgramOwnDir(Environment.GetFolderPath(Environment.SpecialFolder.System)));
        Assert.False(AppUninstallService.IsProgramOwnDir(Path.GetTempPath()));
        Assert.False(AppUninstallService.IsProgramOwnDir(Path.Combine(_dir, "нет-такой")));
        Assert.False(AppUninstallService.IsProgramOwnDir(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles)));
        Assert.False(AppUninstallService.IsProgramOwnDir(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)));
        Assert.False(AppUninstallService.IsProgramOwnDir("."));
        Assert.False(AppUninstallService.IsProgramOwnDir("App"));
        Assert.True(AppUninstallService.IsProgramOwnDir(_dir));
    }

    [Fact]
    public void Msi_ОстаётсяЗаWinget()
        => Assert.False(AppUninstallService.PreferDirectUninstall(true, false, TrustedExecutablePaths.MsiExec));
}
