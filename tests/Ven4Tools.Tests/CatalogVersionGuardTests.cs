using Ven4Tools.Services;

namespace Ven4Tools.Tests;

// DPAPI-защита счётчика anti-rollback: значение должно переживать round-trip через
// Protect/Unprotect, а подделанный/битый ввод — отклоняться (не восприниматься как
// валидная версия). Работает через CurrentUser DPAPI, поэтому тест — Windows-only,
// как и вся кодовая база клиента.
public sealed class CatalogVersionGuardTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(int.MaxValue)]
    public void ProtectThenUnprotect_RoundTrips(int version)
    {
        var blob = CatalogVersionGuard.Protect(version);
        Assert.Equal(version, CatalogVersionGuard.TryUnprotect(blob));
    }

    [Fact]
    public void ProtectedBlob_IsNotPlaintext()
    {
        // Защищённый blob не должен содержать саму цифру версии открытым текстом.
        var blob = CatalogVersionGuard.Protect(1234567);
        Assert.DoesNotContain("1234567", blob);
    }

    [Fact]
    public void TryUnprotect_GarbageInput_ReturnsNull()
    {
        Assert.Null(CatalogVersionGuard.TryUnprotect("это не base64 DPAPI"));
        Assert.Null(CatalogVersionGuard.TryUnprotect(""));
        // Валидный base64, но не DPAPI-blob — тоже отклоняется.
        Assert.Null(CatalogVersionGuard.TryUnprotect("aGVsbG8gd29ybGQ="));
    }
}
