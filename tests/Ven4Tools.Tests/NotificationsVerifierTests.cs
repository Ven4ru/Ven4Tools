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
