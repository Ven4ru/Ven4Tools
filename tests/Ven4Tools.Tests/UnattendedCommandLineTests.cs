using Ven4Tools.Services;
using Status = Ven4Tools.Services.UnattendedCommandLine.ParseStatus;

namespace Ven4Tools.Tests;

/// <summary>
/// Разбор задания тихого режима: командная строка и файл ответа. Ошибка в задании
/// должна останавливать клиент до открытия окна, поэтому все отказы проверяются здесь.
/// </summary>
public class UnattendedCommandLineTests
{
    private static string NoFile(string path) => throw new FileNotFoundException(path);

    private static (Status Status, UnattendedRequest? Request, string Error) Parse(
        string[] args, Func<string, string>? readFile = null)
    {
        var status = UnattendedCommandLine.Parse(args, readFile ?? NoFile, out var request, out string error);
        return (status, request, error);
    }

    [Theory]
    [InlineData()]
    [InlineData("--silent")]
    [InlineData("--from-launcher", "--drive", "D:")]
    public void БезНабора_ОбычныйЗапуск(params string[] args)
    {
        var (status, request, _) = Parse(args);

        Assert.Equal(Status.None, status);
        Assert.Null(request);
    }

    [Fact]
    public void КодНабора_ДаётСписокПриложений()
    {
        var (status, request, _) = Parse(new[] { "--install", "V4T:firefox,7zip", "--silent" });

        Assert.Equal(Status.Ok, status);
        Assert.Equal(new[] { "firefox", "7zip" }, request!.AppIds);
        Assert.True(request.Silent);
        Assert.Null(request.RestorePoint);
        Assert.True(request.AllowPackageManagers);
    }

    [Fact]
    public void СписокБезПрефикса_ТожеПринимается()
    {
        var (status, request, _) = Parse(new[] { "--install", "firefox,telegram" });

        Assert.Equal(Status.Ok, status);
        Assert.Equal(new[] { "firefox", "telegram" }, request!.AppIds);
        Assert.False(request.Silent);
    }

    [Theory]
    [InlineData("--restore-point", true)]
    [InlineData("--no-restore-point", false)]
    public void ТочкаВосстановления_ЗадаётсяФлагом(string flag, bool expected)
    {
        var (_, request, _) = Parse(new[] { "--install", "V4T:vlc", flag });

        Assert.Equal(expected, request!.RestorePoint);
    }

    [Theory]
    [InlineData("d", "D:")]
    [InlineData("D:", "D:")]
    [InlineData("e:\\", "E:")]
    public void Диск_ПриводитсяКБукве(string given, string expected)
    {
        var (status, request, _) = Parse(new[] { "--install", "V4T:vlc", "--drive", given });

        Assert.Equal(Status.Ok, status);
        Assert.Equal(expected, request!.InstallDrive);
    }

    [Theory]
    [InlineData("--install")]                               // нет значения
    [InlineData("--install", "--silent")]                   // вместо значения — другой флаг
    [InlineData("--install", "V4T:vlc", "--drive", "диск")] // диск не буквой
    [InlineData("--install", "V4T:vlc", "--report")]        // нет пути
    [InlineData("--answer-file")]
    public void НеразборчивоеЗадание_Ошибка(params string[] args)
    {
        var (status, request, error) = Parse(args);

        Assert.Equal(Status.Error, status);
        Assert.Null(request);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void ФайлОтвета_ЧитаетсяЦеликом()
    {
        const string json = """
            {
              // комментарии и висящие запятые не мешают
              "apps": ["firefox", "7zip"],
              "silent": true,
              "restorePoint": true,
              "drive": "d:",
              "allowPackageManagers": false,
              "report": "C:\\Temp\\итог.json",
            }
            """;

        var (status, request, _) = Parse(new[] { "--answer-file", "набор.json" }, _ => json);

        Assert.Equal(Status.Ok, status);
        Assert.Equal(new[] { "firefox", "7zip" }, request!.AppIds);
        Assert.True(request.Silent);
        Assert.True(request.RestorePoint);
        Assert.Equal("D:", request.InstallDrive);
        Assert.False(request.AllowPackageManagers);
        Assert.Equal("C:\\Temp\\итог.json", request.ReportPath);
    }

    [Fact]
    public void КоманднаяСтрока_СильнееФайлаОтвета()
    {
        const string json = """{ "code": "V4T:vlc", "restorePoint": true, "drive": "D:", "report": "a.json" }""";

        var (status, request, _) = Parse(
            new[] { "--answer-file", "x.json", "--install", "V4T:firefox", "--no-restore-point", "--drive", "E:", "--report", "b.json" },
            _ => json);

        Assert.Equal(Status.Ok, status);
        // Наборы складываются: строка уточняет файл, а не выбрасывает его состав.
        Assert.Equal(new[] { "firefox", "vlc" }, request!.AppIds);
        Assert.False(request.RestorePoint);
        Assert.Equal("E:", request.InstallDrive);
        Assert.Equal("b.json", request.ReportPath);
    }

    [Theory]
    [InlineData("не json")]
    [InlineData("null")]
    [InlineData("{ \"apps\": [] }")]
    public void ФайлОтвета_БезНабораИлиБитый_Ошибка(string json)
    {
        var (status, _, error) = Parse(new[] { "--answer-file", "x.json" }, _ => json);

        Assert.Equal(Status.Error, status);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void ФайлОтвета_НеНайден_Ошибка()
    {
        var (status, _, error) = Parse(new[] { "--answer-file", "нет.json" });

        Assert.Equal(Status.Error, status);
        Assert.Contains("файл ответа", error);
    }

    [Fact]
    public void Повторы_ВНабореСхлопываются()
    {
        var (_, request, _) = Parse(new[] { "--install", "V4T:vlc,VLC,firefox" });

        Assert.Equal(new[] { "vlc", "firefox" }, request!.AppIds);
    }
}
