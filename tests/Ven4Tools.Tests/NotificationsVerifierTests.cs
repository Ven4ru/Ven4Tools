using System.Text;
using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Tests;

public sealed class NotificationsVerifierTests
{
    [Fact]
    public void SignedFixture_HasValidSignature()
    {
        string json = File.ReadAllText(FixturePath("notifications.json"), Encoding.UTF8);
        string signature = File.ReadAllText(FixturePath("notifications.json.sig"), Encoding.UTF8);

        Assert.True(NotificationsVerifier.Verify(json, signature));
    }

    [Fact]
    public void SignedFixture_HasNoCarriageReturns()
    {
        // Лаунчер скачивает notifications.json напрямую с raw.githubusercontent.com,
        // то есть проверяет байты git-объекта (LF). Подпись же считается по файлу
        // с диска. Если рабочая копия чекаутится с CRLF (Windows + core.autocrlf),
        // подписывается один набор байт, а проверяется другой — и проверка молча
        // падает у всех пользователей, тогда как тест подписи на машине разработчика
        // остаётся зелёным (он читает тот же CRLF-файл). От этого защищает пометка
        // `-text` в .gitattributes; тест фиксирует результат, а не намерение.
        byte[] fixture = File.ReadAllBytes(FixturePath("notifications.json"));

        Assert.DoesNotContain((byte)'\r', fixture);
    }

    [Fact]
    public void ModifiedNotifications_IsRejected()
    {
        string json = File.ReadAllText(FixturePath("notifications.json"), Encoding.UTF8);
        string signature = File.ReadAllText(FixturePath("notifications.json.sig"), Encoding.UTF8);

        Assert.False(NotificationsVerifier.Verify(json + " ", signature));
    }

    [Fact]
    public void UpdateManifestSignature_DoesNotVerifyAsNotifications()
    {
        // Domain separation: подпись version.json (другой ключ, другой префикс)
        // не должна проходить как подпись notifications.json.
        string json = File.ReadAllText(FixturePath("version-manifest-sample.json"), Encoding.UTF8);
        string updateManifestSignature = File.ReadAllText(FixturePath("version-manifest-sample.json.sig"), Encoding.UTF8);

        Assert.False(NotificationsVerifier.Verify(json, updateManifestSignature));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-base64")]
    [InlineData("AA==")]
    public void MalformedOrMissingSignature_IsRejected(string? signature)
    {
        Assert.False(NotificationsVerifier.Verify("{}", signature));
    }

    private static string FixturePath(string fileName)
    {
        return Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
    }
}
