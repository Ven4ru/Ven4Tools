using Ven4Tools.Models;

namespace Ven4Tools.Tests;

/// <summary>Подписи строки «Истории»: русские слова и столбец одной ширины.</summary>
public class HistoryEntryTests
{
    [Theory]
    [InlineData(true, "установлено")]
    [InlineData(false, "не удалось")]
    public void ActionVerb_РусскоеСловоОднойШирины(bool success, string word)
    {
        var entry = new HistoryEntry { Success = success };

        Assert.Equal(word, entry.ActionVerb.TrimEnd());
        Assert.Equal(12, entry.ActionVerb.Length);
    }

    [Theory]
    [InlineData("winget", "📦 Winget")]
    [InlineData("choco", "🍫 Chocolatey")]
    [InlineData("direct", "🔗 Прямая ссылка")]
    [InlineData("cache", "🔌 Кэш")]
    [InlineData("другое", "другое")]
    public void SourceLabel_ПодписьИсточника(string source, string label)
    {
        Assert.Equal(label, new HistoryEntry { Source = source }.SourceLabel);
    }
}
