using Ven4Tools.Services;

namespace Ven4Tools.Tests;

/// <summary>
/// Разбор XML профиля Wi-Fi, который отдаёт WlanGetProfile: имя сети, тип защиты,
/// пароль открытым текстом или «зашифрован — не показываем».
/// </summary>
public sealed class WifiProfileServiceTests
{
    private static string Profile(string auth, string enc, string? keyBlock, string ssidBlock = "<hex>486F6D65</hex><name>Home</name>",
        string nonBroadcast = "false") =>
        $"""
        <?xml version="1.0"?>
        <WLANProfile xmlns="http://www.microsoft.com/networking/WLAN/profile/v1">
          <name>ProfileName</name>
          <SSIDConfig>
            <SSID>{ssidBlock}</SSID>
            <nonBroadcast>{nonBroadcast}</nonBroadcast>
          </SSIDConfig>
          <connectionType>ESS</connectionType>
          <connectionMode>auto</connectionMode>
          <MSM>
            <security>
              <authEncryption>
                <authentication>{auth}</authentication>
                <encryption>{enc}</encryption>
                <useOneX>false</useOneX>
              </authEncryption>
              {keyBlock}
            </security>
          </MSM>
        </WLANProfile>
        """;

    private const string PlainKey =
        "<sharedKey><keyType>passPhrase</keyType><protected>false</protected><keyMaterial>secret;pass</keyMaterial></sharedKey>";

    private const string ProtectedKey =
        "<sharedKey><keyType>passPhrase</keyType><protected>true</protected><keyMaterial>01000000D08C9DDF0115D1118C7A00C04FC297EB</keyMaterial></sharedKey>";

    [Fact]
    public void Wpa2PersonalWithPlaintextKey()
    {
        WifiProfileDetails? d = WifiProfileService.ParseProfileXml(Profile("WPA2PSK", "AES", PlainKey));
        Assert.NotNull(d);
        Assert.Equal("Home", d.Ssid);
        Assert.Equal(WifiSecurityKind.Wpa, d.Security);
        Assert.Equal("secret;pass", d.Password);
        Assert.False(d.KeyProtected);
        Assert.False(d.Hidden);
    }

    [Fact]
    public void ProtectedKeyIsNeverReturnedAsPassword()
    {
        WifiProfileDetails? d = WifiProfileService.ParseProfileXml(Profile("WPA2PSK", "AES", ProtectedKey));
        Assert.NotNull(d);
        Assert.Null(d.Password);
        Assert.True(d.KeyProtected);
    }

    [Fact]
    public void OpenNetworkWithoutKey()
    {
        WifiProfileDetails? d = WifiProfileService.ParseProfileXml(Profile("open", "none", null));
        Assert.NotNull(d);
        Assert.Equal(WifiSecurityKind.Open, d.Security);
        Assert.Null(d.Password);
    }

    [Fact]
    public void HiddenNetworkIsDetected()
    {
        WifiProfileDetails? d = WifiProfileService.ParseProfileXml(Profile("WPA3SAE", "AES", PlainKey, nonBroadcast: "true"));
        Assert.NotNull(d);
        Assert.True(d.Hidden);
        Assert.Equal(WifiSecurityKind.Wpa, d.Security);
    }

    [Fact]
    public void SsidIsDecodedFromHexWhenNameIsMissing()
    {
        // «Дом» в UTF-8.
        WifiProfileDetails? d = WifiProfileService.ParseProfileXml(
            Profile("WPA2PSK", "AES", PlainKey, ssidBlock: "<hex>D094D0BED0BC</hex>"));
        Assert.NotNull(d);
        Assert.Equal("Дом", d.Ssid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<not-closed")]
    public void BrokenXmlGivesNull(string xml)
    {
        Assert.Null(WifiProfileService.ParseProfileXml(xml));
    }

    [Theory]
    [InlineData("open", "none", WifiSecurityKind.Open)]
    [InlineData("open", "WEP", WifiSecurityKind.Wep)]
    [InlineData("shared", "WEP", WifiSecurityKind.Wep)]
    [InlineData("OWE", "AES", WifiSecurityKind.Open)]
    [InlineData("WPAPSK", "TKIP", WifiSecurityKind.Wpa)]
    [InlineData("WPA2PSK", "AES", WifiSecurityKind.Wpa)]
    [InlineData("WPA3SAE", "AES", WifiSecurityKind.Wpa)]
    [InlineData("WPA2", "AES", WifiSecurityKind.Enterprise)]
    [InlineData("WPA3ENT192", "GCMP256", WifiSecurityKind.Enterprise)]
    [InlineData("something-new", "AES", WifiSecurityKind.Unknown)]
    public void SecurityMapping(string auth, string enc, WifiSecurityKind expected)
    {
        Assert.Equal(expected, WifiProfileService.MapSecurity(auth, enc));
    }
}
