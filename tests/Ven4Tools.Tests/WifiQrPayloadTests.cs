using Ven4Tools.Services;

namespace Ven4Tools.Tests;

/// <summary>
/// Строка подключения к Wi-Fi для QR-кода: экранирование служебных символов
/// формата и сборка для разных типов защиты.
/// </summary>
public sealed class WifiQrPayloadTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData(@"a\b", @"a\\b")]
    [InlineData("a;b", @"a\;b")]
    [InlineData("a,b", @"a\,b")]
    [InlineData("a:b", @"a\:b")]
    [InlineData("a\"b", "a\\\"b")]
    [InlineData(@"\;,:""", @"\\\;\,\:\""")]
    [InlineData("", "")]
    [InlineData("Сеть дома", "Сеть дома")]
    public void EscapeAddsBackslashBeforeEverySpecialCharacter(string input, string expected)
    {
        Assert.Equal(expected, WifiQrPayload.Escape(input));
    }

    [Fact]
    public void EscapeLeavesOtherPunctuationAlone()
    {
        Assert.Equal("p@ss w0rd!#$%&'()*+-./<=>?[]^_`{|}~", WifiQrPayload.Escape("p@ss w0rd!#$%&'()*+-./<=>?[]^_`{|}~"));
    }

    [Fact]
    public void WpaNetworkProducesStandardString()
    {
        Assert.Equal("WIFI:T:WPA;S:Home;P:secret123;;",
            WifiQrPayload.Build("Home", "secret123", WifiSecurityKind.Wpa, hidden: false));
    }

    [Fact]
    public void SpecialCharactersInSsidAndPasswordAreEscaped()
    {
        Assert.Equal(@"WIFI:T:WPA;S:my\;net;P:pa\:ss\,word\\;;",
            WifiQrPayload.Build("my;net", @"pa:ss,word\", WifiSecurityKind.Wpa, hidden: false));
    }

    [Fact]
    public void HiddenNetworkAddsHiddenFlag()
    {
        Assert.Equal("WIFI:T:WPA;S:Home;P:secret123;H:true;;",
            WifiQrPayload.Build("Home", "secret123", WifiSecurityKind.Wpa, hidden: true));
    }

    [Fact]
    public void OpenNetworkHasNoPasswordField()
    {
        Assert.Equal("WIFI:T:nopass;S:Cafe;;",
            WifiQrPayload.Build("Cafe", null, WifiSecurityKind.Open, hidden: false));
    }

    [Fact]
    public void WepNetworkUsesWepType()
    {
        Assert.Equal("WIFI:T:WEP;S:Old;P:12345;;",
            WifiQrPayload.Build("Old", "12345", WifiSecurityKind.Wep, hidden: false));
    }

    [Theory]
    [InlineData(WifiSecurityKind.Enterprise)]
    [InlineData(WifiSecurityKind.Unknown)]
    public void NetworksWithoutSharedPasswordGetNoQr(WifiSecurityKind kind)
    {
        Assert.Null(WifiQrPayload.Build("Corp", "whatever", kind, hidden: false));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ProtectedNetworkWithoutPasswordGetsNoQr(string? password)
    {
        Assert.Null(WifiQrPayload.Build("Home", password, WifiSecurityKind.Wpa, hidden: false));
    }
}
