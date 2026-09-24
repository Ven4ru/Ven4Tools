using Ven4Tools.Views.Tabs;

namespace Ven4Tools.Tests;

/// <summary>
/// Разбор строки «Центра активности»: значок отрезается ровно по своей длине.
/// Раньше срезы были зашиты (text[2..]/text[3..]) и не совпадали с длиной
/// однобуквенных значков и значков без U+FE0F.
/// </summary>
public sealed class VwLogEntryParseTests
{
    [Theory]
    [InlineData("✅ Готово", "✅", "Готово")]
    [InlineData("✅Готово", "✅", "Готово")]
    [InlineData("⚠️ Внимание", "⚠️", "Внимание")]
    [InlineData("⚠ Сеть недоступна", "⚠️", "Сеть недоступна")]
    [InlineData("🗑 Удалено", "🗑️", "Удалено")]
    [InlineData("⏳Ждём", "⏳", "Ждём")]
    [InlineData("📦 Пакет", "📦", "Пакет")]
    [InlineData("просто текст", "·", "просто текст")]
    public void ЗначокОтрезаетсяБезПотериТекста(string raw, string icon, string message)
    {
        var entry = LogEntry.Parse(raw);

        Assert.Equal(icon, entry.Icon);
        Assert.Equal(message, entry.Message);
    }

    [Theory]
    [InlineData("✅")]
    [InlineData("❌")]
    [InlineData("➕")]
    [InlineData("⏳")]
    public void СтрокаИзОдногоЗначка_НеБросает(string raw)
    {
        var entry = LogEntry.Parse(raw);

        Assert.Equal(string.Empty, entry.Message);
    }
}
