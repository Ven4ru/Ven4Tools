using Ven4Tools.Launcher.Services;
using Xunit;

namespace Ven4Tools.Tests;

// Установка без подтверждения CDN — только по встроенной подписи архива, и
// никогда не откатом на старую версию и не подменой одной версии другой.
public class SignedArchiveFallbackPolicyTests
{
    [Theory]
    [InlineData("5.2.0", null)]          // клиент не установлен
    [InlineData("5.2.0", "5.1.1.0")]     // обновление
    [InlineData("5.2.0", "5.2.0.0")]     // переустановка той же версии
    public void ДоЗагрузки_НеОткат_Разрешено(string requested, string? installed)
    {
        Assert.Null(SignedArchiveFallbackPolicy.CheckBeforeDownload(requested, installed));
    }

    [Fact]
    public void ДоЗагрузки_Откат_ОтказДоСкачивания()
    {
        string? refusal = SignedArchiveFallbackPolicy.CheckBeforeDownload("5.1.1", "5.2.0.0");

        Assert.NotNull(refusal);
        Assert.Contains("старее установленной", refusal);
    }

    [Theory]
    [InlineData("5.2.0", "5.2.0", null)]
    [InlineData("5.2.0", "5.2.0", "5.1.1.0")]
    [InlineData("5.2.0", "5.2.0", "5.2.0.0")]
    public void ПослеЗагрузки_ПодписьСовпадаетИНеОткат_Разрешено(string signed, string requested, string? installed)
    {
        Assert.Null(SignedArchiveFallbackPolicy.CheckSignedArchive(signed, requested, installed));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ПослеЗагрузки_БезВстроеннойПодписи_Отказ(string? signed)
    {
        string? refusal = SignedArchiveFallbackPolicy.CheckSignedArchive(signed, "5.2.0", null);

        Assert.NotNull(refusal);
        Assert.Contains("нет встроенной подписи", refusal);
    }

    [Fact]
    public void ПослеЗагрузки_ПодписанСтарыйАрхивПодВидомНового_Отказ()
    {
        // Подписанный, но старый архив, выданный источником под видом 5.2.0.
        string? refusal = SignedArchiveFallbackPolicy.CheckSignedArchive("5.1.1", "5.2.0", null);

        Assert.NotNull(refusal);
        Assert.Contains("подменён", refusal);
    }

    [Fact]
    public void ПослеЗагрузки_ПодписьВерна_НоСтарееУстановленной_Отказ()
    {
        string? refusal = SignedArchiveFallbackPolicy.CheckSignedArchive("5.1.1", "5.1.1", "5.2.0.0");

        Assert.NotNull(refusal);
        Assert.Contains("старее установленной", refusal);
    }
}
