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
        var message = WingetErrorMapper.MapExitCode(unchecked((int)0x8A150109));
        Assert.Contains("Хеш", message);
    }

    [Fact]
    public void MapExitCode_НеизвестныйКод_СодержитЧислоИHex()
    {
        var message = WingetErrorMapper.MapExitCode(unchecked((int)0x12345678));
        Assert.Contains("12345678", message);
        Assert.Contains("305419896", message);
    }
}
