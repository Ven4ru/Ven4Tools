using Ven4Tools.Services;

namespace Ven4Tools.Tests;

// Защита от отката (downgrade) каталога: старый валидно подписанный master.json
// с версией ниже последней применённой не должен приниматься повторно.
public sealed class CatalogRollbackTests
{
    [Fact]
    public void FirstRun_NoStoredVersion_AcceptsAnyVersion()
    {
        // lastSeen = 0 — памяти ещё нет, принимается любая версия
        Assert.True(CatalogLoaderService.IsVersionAcceptable(1, 0));
        Assert.True(CatalogLoaderService.IsVersionAcceptable(42, 0));
    }

    [Fact]
    public void NegativeStoredVersion_TreatedAsNoMemory()
    {
        Assert.True(CatalogLoaderService.IsVersionAcceptable(1, -5));
    }

    [Fact]
    public void SameVersion_IsAccepted()
    {
        // Равная версия допустима: переустановка/переприменение того же каталога
        Assert.True(CatalogLoaderService.IsVersionAcceptable(10, 10));
    }

    [Fact]
    public void HigherVersion_IsAccepted()
    {
        Assert.True(CatalogLoaderService.IsVersionAcceptable(11, 10));
    }

    [Fact]
    public void LowerVersion_IsRejected()
    {
        // Строго меньшая версия — откат, отклоняется
        Assert.False(CatalogLoaderService.IsVersionAcceptable(9, 10));
        Assert.False(CatalogLoaderService.IsVersionAcceptable(0, 10));
    }
}
