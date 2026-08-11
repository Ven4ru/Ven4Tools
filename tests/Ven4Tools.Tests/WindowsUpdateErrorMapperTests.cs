using Ven4Tools.Services.WindowsUpdate;

namespace Ven4Tools.Tests;

public sealed class WindowsUpdateErrorMapperTests
{
    [Fact]
    public void MapHResult_KnownCode_ReturnsFriendlyMessage()
    {
        var message = WindowsUpdateErrorMapper.MapHResult(unchecked((int)0x80070422));
        Assert.Contains("отключена", message);
    }

    [Fact]
    public void MapHResult_UnknownCode_ReturnsGenericWithHexCode()
    {
        var message = WindowsUpdateErrorMapper.MapHResult(unchecked((int)0x12345678));
        Assert.Contains("12345678", message);
    }
}
