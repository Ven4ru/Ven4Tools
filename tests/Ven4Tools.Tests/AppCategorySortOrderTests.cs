using Ven4Tools.Models;

namespace Ven4Tools.Tests;

/// <summary>
/// Номер категории хранится в списке пользовательских приложений (UserAppsStore),
/// поэтому новые категории дописываются в конец перечисления, а место в каталоге
/// задаёт AppCategoryHelper.SortOrder.
/// </summary>
public sealed class AppCategorySortOrderTests
{
    [Fact]
    public void Номера_прежних_категорий_не_сдвинулись()
    {
        Assert.Equal(9, (int)AppCategory.Драйверпаки);
        Assert.Equal(10, (int)AppCategory.Другое);
        Assert.Equal(11, (int)AppCategory.Пользовательские);
        Assert.Equal(12, (int)AppCategory.Компоненты);
    }

    [Fact]
    public void Компоненты_в_каталоге_идут_сразу_после_драйверпаков()
    {
        Assert.Equal(AppCategoryHelper.SortOrder(AppCategory.Драйверпаки) + 1,
            AppCategoryHelper.SortOrder(AppCategory.Компоненты));
        Assert.True(AppCategoryHelper.SortOrder(AppCategory.Компоненты) < AppCategoryHelper.SortOrder(AppCategory.Другое));
    }

    [Fact]
    public void У_каждой_категории_своё_место()
    {
        var orders = Enum.GetValues<AppCategory>().Select(AppCategoryHelper.SortOrder).ToList();

        Assert.Equal(orders.Count, orders.Distinct().Count());
    }

    [Fact]
    public void Компоненты_читаются_из_каталога_и_пишутся_обратно()
    {
        Assert.Equal(AppCategory.Компоненты, AppCategoryHelper.Parse("Компоненты"));
        Assert.Equal("Компоненты", new AppInfo { Category = AppCategory.Компоненты }.CategoryString);
    }
}
