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

    [Fact]
    public void GetItemsNeedingEula_OnlySelectedAndUnaccepted()
    {
        var eulaItem = new WindowsUpdateItem
        {
            UpdateId = "1", Title = "Driver", EulaAccepted = false, EulaText = "текст лицензии"
        };
        var noEulaItem = new WindowsUpdateItem { UpdateId = "2", Title = "Patch", EulaAccepted = false, EulaText = "" };
        var acceptedItem = new WindowsUpdateItem
        {
            UpdateId = "3", Title = "Other", EulaAccepted = true, EulaText = "текст"
        };

        var tree = new[]
        {
            new WindowsUpdateCategoryNode
            {
                Name = "X",
                Items =
                {
                    new() { Item = eulaItem, IsChecked = true },
                    new() { Item = noEulaItem, IsChecked = true },
                    new() { Item = acceptedItem, IsChecked = true },
                    new() { Item = new WindowsUpdateItem { UpdateId = "4", EulaText = "текст" }, IsChecked = false }, // не выбран
                }
            }
        };

        var result = WindowsUpdateErrorMapper.GetItemsNeedingEula(tree);

        Assert.Single(result);
        Assert.Equal("1", result[0].UpdateId);
    }
}
