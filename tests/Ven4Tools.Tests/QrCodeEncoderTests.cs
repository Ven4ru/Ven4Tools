using Ven4Tools.Services;

namespace Ven4Tools.Tests;

/// <summary>
/// Структурные проверки собственного кодировщика QR: размер матрицы по версии,
/// поисковые узоры в трёх углах, синхрополосы, табличные величины стандарта.
/// Читаемость кода телефоном проверяется вживую (см. отчёт выпуска).
/// </summary>
public sealed class QrCodeEncoderTests
{
    [Theory]
    [InlineData(1, 26)]
    [InlineData(2, 44)]
    [InlineData(7, 196)]
    [InlineData(10, 346)]
    [InlineData(15, 655)]
    public void RawCodewordsMatchStandardTable(int version, int codewords)
    {
        Assert.Equal(codewords, QrCodeEncoder.RawDataModules(version) / 8);
    }

    [Theory]
    [InlineData(1, 16)]
    [InlineData(5, 86)]
    [InlineData(10, 216)]
    [InlineData(15, 415)]
    public void DataCodewordsForLevelM(int version, int expected)
    {
        Assert.Equal(expected, QrCodeEncoder.DataCodewords(version));
    }

    [Theory]
    [InlineData(1, new int[0])]
    [InlineData(2, new[] { 6, 18 })]
    [InlineData(7, new[] { 6, 22, 38 })]
    [InlineData(14, new[] { 6, 26, 46, 66 })]
    [InlineData(15, new[] { 6, 26, 48, 70 })]
    public void AlignmentPatternPositions(int version, int[] expected)
    {
        Assert.Equal(expected, QrCodeEncoder.AlignmentPositions(version));
    }

    [Fact]
    public void ShortTextFitsVersionOne()
    {
        bool[,] m = QrCodeEncoder.Encode("hello");
        Assert.Equal(21, m.GetLength(0));
        Assert.Equal(21, m.GetLength(1));
    }

    [Fact]
    public void TypicalWifiStringUsesSmallVersion()
    {
        bool[,] m = QrCodeEncoder.Encode("WIFI:T:WPA;S:Home;P:secret123;;");
        Assert.Equal(29, m.GetLength(0)); // версия 3
    }

    [Fact]
    public void LongestWifiStringStillFits()
    {
        // SSID — до 32 байт, пароль WPA — до 63 символов; в худшем случае каждый символ экранирован.
        string ssid = new string(';', 32);
        string pass = new string(';', 63);
        string payload = WifiQrPayload.Build(ssid, pass, WifiSecurityKind.Wpa, hidden: true)!;
        bool[,] m = QrCodeEncoder.Encode(payload);
        Assert.True(m.GetLength(0) <= 15 * 4 + 17);
    }

    [Fact]
    public void TooLongTextThrows()
    {
        Assert.Throws<ArgumentException>(() => QrCodeEncoder.Encode(new string('x', 1000)));
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("WIFI:T:WPA;S:Сеть дома;P:пароль;;")]
    [InlineData("0123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789")]
    public void FinderPatternsAndTimingArePresent(string text)
    {
        bool[,] m = QrCodeEncoder.Encode(text);
        int size = m.GetLength(0);
        AssertFinder(m, 0, 0);
        AssertFinder(m, size - 7, 0);
        AssertFinder(m, 0, size - 7);
        for (int i = 8; i < size - 8; i++)
        {
            Assert.Equal(i % 2 == 0, m[6, i]);
            Assert.Equal(i % 2 == 0, m[i, 6]);
        }
        Assert.True(m[size - 8, 8]); // всегда тёмный модуль
    }

    [Fact]
    public void EncodingIsDeterministic()
    {
        bool[,] a = QrCodeEncoder.Encode("WIFI:T:WPA;S:Home;P:secret123;;");
        bool[,] b = QrCodeEncoder.Encode("WIFI:T:WPA;S:Home;P:secret123;;");
        Assert.Equal(a.Cast<bool>(), b.Cast<bool>());
    }

    private static void AssertFinder(bool[,] m, int top, int left)
    {
        for (int y = 0; y < 7; y++)
        {
            for (int x = 0; x < 7; x++)
            {
                int dist = Math.Max(Math.Abs(x - 3), Math.Abs(y - 3));
                Assert.Equal(dist != 2, m[top + y, left + x]);
            }
        }
    }
}
