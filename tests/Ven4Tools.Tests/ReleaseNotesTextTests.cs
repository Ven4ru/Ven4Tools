using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Tests;

/// <summary>Заметки к выпуску: русская и английская половины одного описания релиза.</summary>
public class ReleaseNotesTextTests
{
    private const string Both = "## Что нового\n- Английский интерфейс\n\n<!-- en -->\n\n## What's new\n- English interface\n";

    [Fact]
    public void RussianInterface_GetsTextBeforeMarker()
    {
        Assert.Equal("## Что нового\n- Английский интерфейс", ReleaseNotesText.ForLanguage(Both, "ru"));
    }

    [Fact]
    public void EnglishInterface_GetsTextAfterMarker()
    {
        Assert.Equal("## What's new\n- English interface", ReleaseNotesText.ForLanguage(Both, "en"));
    }

    [Theory]
    [InlineData("ru")]
    [InlineData("en")]
    public void NotesWithoutMarker_AreShownWhole(string language)
    {
        const string old = "## Исправлено\n- Одно\n- Другое";
        Assert.Equal(old, ReleaseNotesText.ForLanguage(old, language));
    }

    [Fact]
    public void MissingHalf_FallsBackToTheOtherLanguage()
    {
        Assert.Equal("Только по-русски", ReleaseNotesText.ForLanguage("Только по-русски\n<!-- en -->\n", "en"));
        Assert.Equal("English only", ReleaseNotesText.ForLanguage("<!-- en -->\nEnglish only", "ru"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void EmptyNotes_StayEmpty(string? notes)
    {
        Assert.Equal(notes, ReleaseNotesText.ForLanguage(notes, "en"));
    }

    [Fact]
    public void Marker_IsCaseInsensitive()
    {
        Assert.Equal("EN", ReleaseNotesText.ForLanguage("RU\n<!-- EN -->\nEN", "en"));
    }
}
