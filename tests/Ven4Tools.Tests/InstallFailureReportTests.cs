using System.Globalization;
using Newtonsoft.Json;
using Ven4Tools.Services;

namespace Ven4Tools.Tests;

// Коллекция общая с остальными тестами семафора установки: CanRetry читает
// InstallationService.IsBusy, параллельный тест с захваченным семафором сделал бы
// результат плавающим.
[Collection("InstallSemaphore")]
public sealed class InstallFailureReportTests
{
    private static InstallFailure Record(string appId, string method, string error, string timestamp) =>
        new() { AppId = appId, AppName = appId, Method = method, Error = error, Timestamp = timestamp };

    private static string Stamp(DateTime utc) => utc.ToString("O", CultureInfo.InvariantCulture);

    [Fact]
    public void FindLatest_ReturnsMostRecentRecordOfThisApp()
    {
        var start = new DateTime(2026, 7, 29, 10, 0, 0, DateTimeKind.Utc);
        var journal = new List<InstallFailure>
        {
            Record("7zip.7zip", "winget", "первая попытка", Stamp(start.AddMinutes(1))),
            Record("Mozilla.Firefox", "direct", "чужая запись", Stamp(start.AddMinutes(2))),
            Record("7zip.7zip", "choco", "вторая попытка", Stamp(start.AddMinutes(3)))
        };

        var found = InstallFailureReport.FindLatest(journal, "7zip.7zip", start);

        Assert.NotNull(found);
        Assert.Equal("choco", found!.Method);
        Assert.Equal("вторая попытка", found.Error);
    }

    [Fact]
    public void FindLatest_IgnoresRecordsFromPreviousSessions()
    {
        var start = new DateTime(2026, 7, 29, 10, 0, 0, DateTimeKind.Utc);
        var journal = new List<InstallFailure>
        {
            Record("7zip.7zip", "winget", "прошлый сеанс", Stamp(start.AddDays(-3)))
        };

        Assert.Null(InstallFailureReport.FindLatest(journal, "7zip.7zip", start));
    }

    [Fact]
    public void FindLatest_IgnoresBrokenTimestamps()
    {
        var start = new DateTime(2026, 7, 29, 10, 0, 0, DateTimeKind.Utc);
        var journal = new List<InstallFailure>
        {
            Record("7zip.7zip", "winget", "битая метка", "не дата")
        };

        Assert.Null(InstallFailureReport.FindLatest(journal, "7zip.7zip", start));
    }

    [Fact]
    public void FindLatest_ReturnsNull_ForEmptyInput()
    {
        var start = new DateTime(2026, 7, 29, 10, 0, 0, DateTimeKind.Utc);

        Assert.Null(InstallFailureReport.FindLatest(null, "7zip.7zip", start));
        Assert.Null(InstallFailureReport.FindLatest(new List<InstallFailure>(), "7zip.7zip", start));
        Assert.Null(InstallFailureReport.FindLatest(
            new List<InstallFailure> { Record("7zip.7zip", "winget", "ошибка", Stamp(start)) }, "", start));
    }

    [Fact]
    public void FindLatest_AcceptsRecordWrittenExactlyAtBatchStart()
    {
        var start = new DateTime(2026, 7, 29, 10, 0, 0, DateTimeKind.Utc);
        var journal = new List<InstallFailure> { Record("7zip.7zip", "winget", "ошибка", Stamp(start)) };

        Assert.NotNull(InstallFailureReport.FindLatest(journal, "7zip.7zip", start));
    }

    [Theory]
    [InlineData("winget", "Winget")]
    [InlineData("choco", "Chocolatey")]
    [InlineData("direct", "Прямая ссылка")]
    [InlineData("local", "Локальный установщик")]
    [InlineData("cache", "Офлайн-кэш")]
    [InlineData("all-sources", "Все источники")]
    [InlineData("validation", "Проверка идентификатора")]
    public void MethodLabel_TranslatesEveryMethodWrittenByInstaller(string method, string expected)
    {
        Assert.Equal(expected, InstallFailureReport.MethodLabel(method));
    }

    [Fact]
    public void MethodLabel_HandlesUnknownAndEmpty()
    {
        Assert.Equal("Неизвестен", InstallFailureReport.MethodLabel(null));
        Assert.Equal("Неизвестен", InstallFailureReport.MethodLabel("   "));
        Assert.Equal("новый-источник", InstallFailureReport.MethodLabel(" новый-источник "));
    }

    [Fact]
    public async Task CanRetry_FollowsInstallSemaphore()
    {
        Assert.True(InstallFailureReport.CanRetry(retryInProgress: false));
        Assert.False(InstallFailureReport.CanRetry(retryInProgress: true));

        await InstallationService.InstallSemaphore.WaitAsync();
        try
        {
            // Пока идёт любая другая установка (каталог, карточка, история, Windows
            // Update) — повтор запрещён, параллельного msiexec быть не должно.
            Assert.False(InstallFailureReport.CanRetry(retryInProgress: false));
        }
        finally
        {
            InstallationService.InstallSemaphore.Release();
        }

        Assert.True(InstallFailureReport.CanRetry(retryInProgress: false));
    }

    // Файл failed_installs.json — общий контракт с лаунчером (у него своя копия
    // модели). Показ сбоев в клиенте не должен менять набор полей на диске.
    [Fact]
    public void InstallFailure_KeepsOnDiskFieldNames()
    {
        string json = JsonConvert.SerializeObject(new InstallFailure());

        foreach (string field in new[]
                 { "SessionId", "AppName", "AppId", "Method", "Error", "Version", "OsVersion", "Timestamp", "Reported" })
        {
            Assert.Contains($"\"{field}\"", json, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void FailuresPath_StaysWhereLauncherLooksForIt()
    {
        Assert.EndsWith(
            Path.Combine("Ven4Tools", "failed_installs.json"),
            InstallFailureService.FailuresPath,
            StringComparison.OrdinalIgnoreCase);
    }
}
