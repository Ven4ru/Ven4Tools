extern alias client;

using client::Ven4Tools.Localization;
using Ven4Tools.Models;
using Ven4Tools.Services;

namespace Ven4Tools.Tests;

/// <summary>
/// Итоговая причина неудачи цепочки источников называет каждый источник. Раньше в
/// отчёт попадала только последняя причина: «choco завершился с кодом 404» — и ни
/// слова о том, что до этого не справился winget, а прямая ссылка была пропущена.
/// </summary>
public sealed class SourceFailureSummaryTests
{
    private static readonly string Root = FindRepositoryRoot();

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Ven4Tools.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Не найден корень репозитория (Ven4Tools.sln).");
    }

    [Fact]
    public void Build_НазываетПричинуКаждогоИсточника()
    {
        var summary = new SourceFailureSummary();
        summary.Record(SourceOrderSettings.Winget, "Ошибка сети — соединение разорвано, попробуйте позже.");
        summary.Record(SourceOrderSettings.Choco, ChocoErrorMapper.MapExitCode(404));
        summary.Record(SourceOrderSettings.Direct, "пропущена, в каталоге нет SHA256");

        Assert.Equal(
            "все источники исчерпаны — Winget: Ошибка сети — соединение разорвано, попробуйте позже." +
            " · Chocolatey: Chocolatey не смог скачать установщик — сервер загрузки недоступен или соединение оборвалось." +
            " · Прямая ссылка: пропущена, в каталоге нет SHA256",
            summary.Build());
    }

    [Fact]
    public void Build_ПорядокИсточниковНеЗависитОтПорядкаПопыток()
    {
        var summary = new SourceFailureSummary();
        summary.Record(SourceOrderSettings.Direct, "прямая");
        summary.Record(SourceOrderSettings.Choco, "choco");
        summary.Record(SourceOrderSettings.Winget, "winget");

        Assert.Equal(
            "все источники исчерпаны — Winget: winget · Chocolatey: choco · Прямая ссылка: прямая",
            summary.Build());
    }

    [Fact]
    public void Build_ИсточникВнеПорядкаУстановки_ПомеченКакНеПробовавшийся()
    {
        var summary = new SourceFailureSummary();
        summary.Record(SourceOrderSettings.Winget, "Пакет не найден в источнике или недоступен для этой системы.");

        string text = summary.Build();

        Assert.Contains($"Chocolatey: {SourceFailureSummary.NotTried}", text);
        Assert.Contains($"Прямая ссылка: {SourceFailureSummary.NotTried}", text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Record_ПопыткаБезРасшифровки_ОтсылаетКЛогу(string? detail)
    {
        // Источник пробовался, но читаемой причины у него нет (winget не запустился,
        // у choco синтетический код -1). «Не пробовался» здесь было бы неправдой.
        var summary = new SourceFailureSummary();
        summary.Record(SourceOrderSettings.Choco, detail);

        Assert.Contains($"Chocolatey: {SourceFailureSummary.SeeLog}", summary.Build());
    }

    [Fact]
    public void Record_НеизвестныйИсточник_НеМеняетИтог()
    {
        var summary = new SourceFailureSummary();
        string before = summary.Build();

        summary.Record("scoop", "давно удалённый источник");
        summary.Record(null, "источник без имени");

        Assert.Equal(before, summary.Build());
    }

    [Fact]
    public void Build_ПереводитсяЦеликом_ВместеСПричинамиВнутри()
    {
        // Причины сами содержат тире и двоеточия. Перевод не должен разрезать строку
        // по ним: у каждой причины своё место в шаблоне, и она переводится отдельно.
        var pack = LanguagePack.Parse(File.ReadAllBytes(
            Path.Combine(Root, "Localization", "packs", "client-en.json")));
        var summary = new SourceFailureSummary();
        summary.Record(SourceOrderSettings.Winget, WingetErrorMapper.MapExitCode(unchecked((int)0x80072EFE)));
        summary.Record(SourceOrderSettings.Choco, ChocoErrorMapper.MapExitCode(404));
        summary.Record(SourceOrderSettings.Direct, "пропущена, в каталоге нет SHA256");

        string translated = pack.Translate(summary.Build());

        Assert.StartsWith("all sources exhausted — Winget: ", translated);
        Assert.Contains(" · Chocolatey: ", translated);
        Assert.Contains(" · Direct link: ", translated);
        Assert.DoesNotMatch("[А-Яа-яЁё]", translated);
    }
}
