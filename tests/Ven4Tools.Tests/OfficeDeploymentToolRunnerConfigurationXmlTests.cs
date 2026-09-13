using System.Xml.Linq;
using Ven4Tools.Services;

namespace Ven4Tools.Tests;

public sealed class OfficeDeploymentToolRunnerConfigurationXmlTests
{
    [Fact]
    public void ПолноеУдаление_СодержитRemoveAll()
    {
        var xml = XDocument.Parse(OfficeDeploymentToolRunner.BuildRemoveAllConfigurationXml());

        var remove = xml.Root!.Element("Remove")!;
        Assert.Equal("TRUE", remove.Attribute("All")?.Value);
        Assert.Empty(remove.Elements("Product"));
    }

    [Fact]
    public void ПолноеУдаление_БезДиалогаИСПринятиемEula()
    {
        var xml = XDocument.Parse(OfficeDeploymentToolRunner.BuildRemoveAllConfigurationXml());

        var display = xml.Root!.Element("Display")!;
        Assert.Equal("None", display.Attribute("Level")?.Value);
        Assert.Equal("TRUE", display.Attribute("AcceptEULA")?.Value);
    }

    [Fact]
    public void АдресноеУдаление_ПереЧисляетКаждыйProductId()
    {
        var xml = XDocument.Parse(
            OfficeDeploymentToolRunner.BuildRemoveProductsConfigurationXml(new[] { "O365ProPlusRetail", "ProPlus2024Retail" }));

        var products = xml.Root!.Element("Remove")!.Elements("Product").ToList();
        Assert.Equal(2, products.Count);
        Assert.Equal("O365ProPlusRetail", products[0].Attribute("ID")?.Value);
        Assert.Equal("ProPlus2024Retail", products[1].Attribute("ID")?.Value);
        // Адресное удаление не должно ставить Remove All="TRUE" — иначе это не
        // адресное удаление, а полное под другим названием.
        Assert.Null(xml.Root!.Element("Remove")!.Attribute("All"));
    }

    [Fact]
    public void АдресноеУдаление_ПустойСписок_Бросает()
    {
        Assert.Throws<ArgumentException>(() =>
            OfficeDeploymentToolRunner.BuildRemoveProductsConfigurationXml(Array.Empty<string>()));
    }
}
