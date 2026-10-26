using Ven4Tools.Helpers;

namespace Ven4Tools.Tests;

/// <summary>
/// Отчёт id 6: FileNotFoundException из GlyphTypeface закрывал клиент. Сбой шрифта
/// гасится, но при непрерывном повторе клиент, как и раньше, завершается.
/// </summary>
public sealed class FontFileFailureGuardTests
{
    private const string GlyphStack =
        "   at MS.Internal.Text.TextInterface.Native.Util.ConvertHresultToException(Int32 hr)\n" +
        "   at System.Windows.Media.GlyphTypeface.GetGlyphMetricsOptimized(CharacterBufferRange characters)\n" +
        "   at System.Windows.Controls.TextBlock.MeasureOverride(Size constraint)";

    private sealed class FakeFileNotFound : FileNotFoundException
    {
        private readonly string _stack;
        public FakeFileNotFound(string stack) : base("Unable to find the specified file.") => _stack = stack;
        public override string StackTrace => _stack;
    }

    [Fact]
    public void ФайлШрифтаИзGlyphTypeface_Распознаётся()
    {
        Assert.True(FontFileFailureGuard.IsMissingFontFile(new FakeFileNotFound(GlyphStack)));
    }

    [Fact]
    public void ОбычныйFileNotFound_НеСчитаетсяСбоемШрифта()
    {
        var ex = new FakeFileNotFound("   at Ven4Tools.Services.SettingsService.Load()");

        Assert.False(FontFileFailureGuard.IsMissingFontFile(ex));
    }

    [Fact]
    public void ДругоеИсключениеСТемЖеСтеком_НеСчитаетсяСбоемШрифта()
    {
        Assert.False(FontFileFailureGuard.IsMissingFontFile(new InvalidOperationException("x")));
    }

    [Fact]
    public void РедкиеСбои_ГасятсяВсе()
    {
        var now = new DateTime(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);
        var guard = new FontFileFailureGuard(() => now);

        for (int i = 0; i < FontFileFailureGuard.MaxFailures * 3; i++)
        {
            Assert.True(guard.TrySuppress(new FakeFileNotFound(GlyphStack)));
            now += FontFileFailureGuard.Window;
        }
    }

    [Fact]
    public void НепрерывныеСбои_ПослеПорогаНеГасятся()
    {
        var now = new DateTime(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);
        var guard = new FontFileFailureGuard(() => now);

        for (int i = 0; i < FontFileFailureGuard.MaxFailures; i++)
            Assert.True(guard.TrySuppress(new FakeFileNotFound(GlyphStack)));

        Assert.False(guard.TrySuppress(new FakeFileNotFound(GlyphStack)));
    }

    [Fact]
    public void НеШрифтовоеИсключение_НеГасится()
    {
        var guard = new FontFileFailureGuard();

        Assert.False(guard.TrySuppress(new InvalidOperationException("x")));
    }
}
