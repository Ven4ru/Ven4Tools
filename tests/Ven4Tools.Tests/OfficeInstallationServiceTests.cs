using Ven4Tools.Services;

namespace Ven4Tools.Tests;

/// <summary>
/// Разбор состояния Office из реестра — реестр подменён, тестируется только логика
/// выбора C2R/MSI/NotFound и разбор значений. Фикстура ClickToRun-ветки — реальные
/// значения, снятые с живой машины при планировании (docs/superpowers/plans/
/// 2026-09-14-office-uninstall-and-version-change-plan.md).
/// </summary>
public sealed class OfficeInstallationServiceTests
{
    private sealed class FakeRegistry : IOfficeRegistryReader
    {
        public Dictionary<string, string> ClickToRunValues { get; } = new();
        public List<(string DisplayName, string DisplayVersion)> UninstallEntries { get; } = new();

        public IReadOnlyDictionary<string, string> ReadClickToRunConfiguration() => ClickToRunValues;
        public IReadOnlyList<(string DisplayName, string DisplayVersion)> ReadUninstallDisplayEntries() => UninstallEntries;
    }

    [Fact]
    public void C2R_РеальнаяКонфигурацияСДвумяProductId_РазбираетсяПолностью()
    {
        var registry = new FakeRegistry();
        registry.ClickToRunValues["ProductReleaseIds"] = "O365ProPlusRetail,ProPlus2024Retail";
        registry.ClickToRunValues["Platform"] = "x64";
        registry.ClickToRunValues["ClientCulture"] = "ru-ru";
        registry.ClickToRunValues["VersionToReport"] = "16.0.20326.20144";

        var info = new OfficeInstallationService(registry).Detect();

        Assert.Equal(OfficeInstallationKind.ClickToRun, info.Kind);
        Assert.Equal("x64", info.Platform);
        Assert.Equal("ru-ru", info.Culture);
        Assert.Equal("16.0.20326.20144", info.Version);
        Assert.Equal(new[] { "O365ProPlusRetail", "ProPlus2024Retail" }, info.ProductIds);
    }

    [Fact]
    public void C2R_ОдинProductId_РазбираетсяКакОдноэлементныйСписок()
    {
        var registry = new FakeRegistry();
        registry.ClickToRunValues["ProductReleaseIds"] = "ProPlusRetail";
        registry.ClickToRunValues["Platform"] = "x64";
        registry.ClickToRunValues["ClientCulture"] = "en-us";
        registry.ClickToRunValues["VersionToReport"] = "16.0.17928.20114";

        var info = new OfficeInstallationService(registry).Detect();

        Assert.Equal(OfficeInstallationKind.ClickToRun, info.Kind);
        Assert.Equal(new[] { "ProPlusRetail" }, info.ProductIds);
    }

    [Fact]
    public void НетC2RИНетUninstallЗаписей_ВозвращаетNotFound()
    {
        var info = new OfficeInstallationService(new FakeRegistry()).Detect();

        Assert.Equal(OfficeInstallationKind.NotFound, info.Kind);
        Assert.Empty(info.ProductIds);
    }

    [Fact]
    public void НетC2R_НоЕстьMsiOfficeВUninstall_ВозвращаетMsi()
    {
        var registry = new FakeRegistry();
        registry.UninstallEntries.Add(("7-Zip", "23.01"));
        registry.UninstallEntries.Add(("Microsoft Office Professional Plus 2013", "15.0.5361.1000"));

        var info = new OfficeInstallationService(registry).Detect();

        Assert.Equal(OfficeInstallationKind.Msi, info.Kind);
        Assert.Equal("Microsoft Office Professional Plus 2013", info.DisplayName);
        Assert.Equal("15.0.5361.1000", info.Version);
        Assert.Empty(info.ProductIds); // MSI не даёт ProductReleaseIds — адресное удаление для него недоступно
    }

    [Fact]
    public void C2RПрисутствует_MSI_ЗаписиИгнорируются()
    {
        // C2R — приоритетный источник истины: если он есть, Uninstall-записи
        // (которые C2R тоже создаёт себе, помимо классических MSI) не должны
        // задваивать результат или переключать Kind на Msi.
        var registry = new FakeRegistry();
        registry.ClickToRunValues["ProductReleaseIds"] = "O365ProPlusRetail";
        registry.ClickToRunValues["Platform"] = "x64";
        registry.ClickToRunValues["ClientCulture"] = "ru-ru";
        registry.ClickToRunValues["VersionToReport"] = "16.0.20326.20144";
        registry.UninstallEntries.Add(("Microsoft Office Professional Plus 2013", "15.0.5361.1000"));

        var info = new OfficeInstallationService(registry).Detect();

        Assert.Equal(OfficeInstallationKind.ClickToRun, info.Kind);
    }

    [Fact]
    public void НеофисныеЗаписиUninstall_НеДаютЛожногоMsi()
    {
        var registry = new FakeRegistry();
        registry.UninstallEntries.Add(("Google Chrome", "130.0.0.0"));
        registry.UninstallEntries.Add(("Microsoft Visual C++ 2015-2022 Redistributable", "14.40"));

        var info = new OfficeInstallationService(registry).Detect();

        Assert.Equal(OfficeInstallationKind.NotFound, info.Kind);
    }

    [Fact]
    public void C2R_БитыйProductReleaseIds_НеПадаетИНеСообщаетClickToRun()
    {
        // "," проходит IsNullOrWhiteSpace, но после RemoveEmptyEntries|TrimEntries даёт
        // пустой массив — DescribeC2R([0]) должен был бы упасть, а Kind = ClickToRun
        // с пустым ProductIds нарушил бы инвариант "адресное удаление доступно всегда,
        // когда Kind == ClickToRun".
        var registry = new FakeRegistry();
        registry.ClickToRunValues["ProductReleaseIds"] = ",";
        registry.ClickToRunValues["Platform"] = "x64";
        registry.ClickToRunValues["ClientCulture"] = "ru-ru";
        registry.ClickToRunValues["VersionToReport"] = "16.0.20326.20144";

        var info = new OfficeInstallationService(registry).Detect();

        // M3: с пустым ProductReleaseIds и пустым UninstallEntries единственный
        // корректный исход — NotFound (а не любой Kind, отличный от ClickToRun: MSI был
        // бы столь же неверным сигналом, как и ClickToRun с пустым ProductIds).
        Assert.Equal(OfficeInstallationKind.NotFound, info.Kind);
        Assert.Empty(info.ProductIds);
    }

    [Fact]
    public void MSI_КомпонентПередСамимПакетомВUninstall_ВыбираетПакет()
    {
        // Компонентная запись стоит ПЕРВОЙ — против старого FirstOrDefault этот тест
        // не проходит, потому что DisplayName/Kind брались бы с языкового пакета.
        var registry = new FakeRegistry();
        registry.UninstallEntries.Add(("Microsoft Office Shared MUI (Russian) 2013", "15.0.5361.1000"));
        registry.UninstallEntries.Add(("Microsoft Office Professional Plus 2013", "15.0.5361.1000"));

        var info = new OfficeInstallationService(registry).Detect();

        Assert.Equal(OfficeInstallationKind.Msi, info.Kind);
        Assert.Equal("Microsoft Office Professional Plus 2013", info.DisplayName);
    }
}
