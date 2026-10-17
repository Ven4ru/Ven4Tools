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
}
