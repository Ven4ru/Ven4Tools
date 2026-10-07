using Ven4Tools.Launcher;
using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Tests;

/// <summary>
/// Понижение версии при установке из локального архива: подписанный архив старой
/// сборки подлинный, но ставить его молча нельзя. Проверяется само решение
/// «понижение или нет» и тексты, которые увидит пользователь и скрипт.
/// </summary>
public sealed class ClientDowngradePolicyTests
{
    [Theory]
    [InlineData("6.0.2", null, null)]            // клиент не установлен, опубликованная неизвестна
    [InlineData("6.0.2", "6.0.1.0", "6.0.2")]    // обновление до текущей
    [InlineData("6.0.2", "6.0.2.0", "6.0.2")]    // переустановка текущей
    [InlineData("6.0.3", "6.0.2.0", "6.0.2")]    // архив новее опубликованной (CDN отстал)
    [InlineData("6.0.2", "", "")]                // пустые версии — сравнивать не с чем
    public void НеСтарееУстановленнойИОпубликованной_НеПонижение(
        string archive, string? installed, string? published)
    {
        Assert.Null(ClientDowngradePolicy.Check(archive, installed, published));
    }

    [Fact]
    public void СтарееУстановленной_Понижение()
    {
        var finding = ClientDowngradePolicy.Check("6.0.0", "6.0.2.0", publishedVersion: null);

        Assert.NotNull(finding);
        Assert.Equal("6.0.2.0", finding!.Value.InstalledVersion);
        Assert.Null(finding.Value.PublishedVersion);
        Assert.Contains("более старая версия 6.0.0", finding.Value.Describe());
        Assert.Contains("установлена 6.0.2.0", finding.Value.Describe());
    }

    [Fact]
    public void КлиентНеУстановлен_НоАрхивСтарееОпубликованной_Понижение()
    {
        var finding = ClientDowngradePolicy.Check("6.0.0", installedVersion: null, "6.0.2");

        Assert.NotNull(finding);
        Assert.Null(finding!.Value.InstalledVersion);
        Assert.Equal("6.0.2", finding.Value.PublishedVersion);
        Assert.Contains("опубликована 6.0.2", finding.Value.Describe());
    }

    [Fact]
    public void УстановленаСтараяНоАрхивЕщёСтарееОпубликованной_Понижение()
    {
        // Установлена 5.9.0, ставят 6.0.0, а опубликована уже 6.0.2: относительно
        // диска это обновление, но сборка всё равно устаревшая.
        var finding = ClientDowngradePolicy.Check("6.0.0", "5.9.0.0", "6.0.2");

        Assert.NotNull(finding);
        Assert.Null(finding!.Value.InstalledVersion);
        Assert.Equal("6.0.2", finding.Value.PublishedVersion);
    }

    [Fact]
    public void СтарееИУстановленнойИОпубликованной_НазываетОбе()
    {
        var finding = ClientDowngradePolicy.Check("5.9.0", "6.0.1.0", "6.0.2");

        Assert.NotNull(finding);
        string text = finding!.Value.Describe();
        Assert.Contains("установлена 6.0.1.0", text);
        Assert.Contains("опубликована 6.0.2", text);
    }

    [Fact]
    public void УстановленаИОпубликованаОднаВерсия_НеПовторяетЕёДважды()
    {
        var finding = ClientDowngradePolicy.Check("6.0.0", "6.0.2.0", "6.0.2");

        Assert.NotNull(finding);
        Assert.Contains("установлена и опубликована 6.0.2", finding!.Value.Describe());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void ВерсияАрхиваНеизвестна_НеПонижение(string? archive)
    {
        Assert.Null(ClientDowngradePolicy.Check(archive, "6.0.2.0", "6.0.2"));
    }

    [Theory]
    [InlineData("6.0.1", "6.0.2.0", true)]
    [InlineData("6.0.2", "6.0.2.0", false)]
    [InlineData("6.0.3", "6.0.2.0", false)]
    [InlineData("6.0.2", null, false)]           // клиент не установлен
    [InlineData(null, "6.0.2.0", false)]
    // В метаданных exe предрелизной метки нет: «6.1.0-beta» на диске — это
    // «6.1.0.0», и повторная установка той же сборки понижением не является.
    [InlineData("6.1.0-beta", "6.1.0.0", false)]
    [InlineData("6.0.9-beta", "6.1.0.0", true)]
    public void СтарееЛиУстановленной(string? candidate, string? installed, bool expected)
    {
        Assert.Equal(expected, ClientDowngradePolicy.IsOlderThanInstalled(candidate, installed));
    }

    [Fact]
    public void ПредварительнаяСборкаСтарееОпубликованнойСтабильнойТогоЖеНомера()
    {
        Assert.NotNull(ClientDowngradePolicy.Check("6.1.0-beta", installedVersion: null, "6.1.0"));
    }

    [Theory]
    [InlineData("6.0.2", "6.0.1", "6.0.2", null)]
    [InlineData("6.0.3", "6.0.3", "6.0.2", "")]
    [InlineData("6.0.2", null, "6.0.2", "  ")]
    public void СамаяНоваяИзИзвестных(string expected, string? a, string? b, string? c)
    {
        Assert.Equal(expected, ClientDowngradePolicy.Newest(a, b, c));
    }

    [Fact]
    public void СамаяНовая_НиОднаНеИзвестна_Null()
    {
        Assert.Null(ClientDowngradePolicy.Newest(null, "", "  "));
    }

    [Fact]
    public void ВопросПользователю_НазываетВерсииИПоследствия()
    {
        var finding = ClientDowngradePolicy.Check("6.0.0", "6.0.2.0", "6.0.2")!.Value;

        string question = ClientDowngradePolicy.BuildQuestion(finding);

        Assert.Contains("6.0.0", question);
        Assert.Contains("6.0.2", question);
        Assert.Contains("Старые сборки не получают исправлений", question);
        Assert.EndsWith("Всё равно установить?", question);
    }

    [Fact]
    public void ОтказКоманднойСтроки_НазываетКлючКоторыйЕгоСнимает()
    {
        var finding = ClientDowngradePolicy.Check("6.0.0", "6.0.2.0", null)!.Value;

        string refusal = ClientDowngradePolicy.BuildRefusal(finding);

        Assert.Contains("6.0.0", refusal);
        Assert.Contains("Установка отменена", refusal);
        Assert.Contains("--allow-downgrade", refusal);
        // Одна строка: уходит в поток ошибок и в журнал лаунчера.
        Assert.DoesNotContain("\n", refusal);
    }

    [Fact]
    public void КодВозврата_ОтказИзЗаПониженияОтличимОтОшибки()
    {
        Assert.Equal(0, CliInstallRunner.ToExitCode(LocalArchiveInstallStatus.Installed));
        Assert.Equal(1, CliInstallRunner.ToExitCode(LocalArchiveInstallStatus.Failed));
        Assert.Equal(4, CliInstallRunner.ToExitCode(LocalArchiveInstallStatus.DowngradeRefused));
    }
}
