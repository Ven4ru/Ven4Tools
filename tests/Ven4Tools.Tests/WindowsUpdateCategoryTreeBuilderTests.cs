using Ven4Tools.Services.WindowsUpdate;

namespace Ven4Tools.Tests;

public sealed class WindowsUpdateCategoryTreeBuilderTests
{
    private static WindowsUpdateItem MakeItem(string id, string title, string category, long size = 1000) =>
        new() { UpdateId = id, Title = title, CategoryNames = new[] { category }, SizeBytes = size };

    [Fact]
    public void Build_GroupsItemsByCategory()
    {
        var items = new[]
        {
            MakeItem("1", "A", "Security Updates"),
            MakeItem("2", "B", "Security Updates"),
            MakeItem("3", "C", "Drivers"),
        };

        var tree = WindowsUpdateCategoryTreeBuilder.Build(items);

        Assert.Equal(2, tree.Count);
        Assert.Equal(2, tree.First(c => c.Name == "Security Updates").Items.Count);
        Assert.Single(tree.First(c => c.Name == "Drivers").Items);
    }

    [Fact]
    public void Build_ItemWithoutCategory_GoesToOther()
    {
        var item = new WindowsUpdateItem { UpdateId = "1", Title = "A" };
        var tree = WindowsUpdateCategoryTreeBuilder.Build(new[] { item });

        Assert.Single(tree);
        Assert.Equal("Другое", tree[0].Name);
    }

    [Fact]
    public void RecalculateCategoryState_AllChecked_ReturnsTrue()
    {
        var category = new WindowsUpdateCategoryNode
        {
            Name = "X",
            Items = { new() { Item = MakeItem("1", "A", "X"), IsChecked = true } }
        };

        WindowsUpdateCategoryTreeBuilder.RecalculateCategoryState(category);

        Assert.True(category.IsChecked);
    }

    [Fact]
    public void RecalculateCategoryState_PartiallyChecked_ReturnsNull()
    {
        var category = new WindowsUpdateCategoryNode
        {
            Name = "X",
            Items =
            {
                new() { Item = MakeItem("1", "A", "X"), IsChecked = true },
                new() { Item = MakeItem("2", "B", "X"), IsChecked = false },
            }
        };

        WindowsUpdateCategoryTreeBuilder.RecalculateCategoryState(category);

        Assert.Null(category.IsChecked);
    }

    [Fact]
    public void ApplyCategoryCheck_SetsAllItemsAndCategory()
    {
        var category = new WindowsUpdateCategoryNode
        {
            Name = "X",
            Items =
            {
                new() { Item = MakeItem("1", "A", "X"), IsChecked = false },
                new() { Item = MakeItem("2", "B", "X"), IsChecked = false },
            }
        };

        WindowsUpdateCategoryTreeBuilder.ApplyCategoryCheck(category, true);

        Assert.All(category.Items, i => Assert.True(i.IsChecked));
        Assert.True(category.IsChecked);
    }

    [Fact]
    public void GetSelectedUpdateIds_DeduplicatesAcrossCategories()
    {
        // Один и тот же патч в двух категориях (например, и Security, и Critical) —
        // не должен попасть в список выбранных дважды.
        var itemInTwoCategories = new WindowsUpdateItem
        {
            UpdateId = "1", Title = "A", CategoryNames = new[] { "Security Updates", "Critical Updates" }
        };
        var tree = WindowsUpdateCategoryTreeBuilder.Build(new[] { itemInTwoCategories });
        foreach (var c in tree) WindowsUpdateCategoryTreeBuilder.ApplyCategoryCheck(c, true);

        var ids = WindowsUpdateCategoryTreeBuilder.GetSelectedUpdateIds(tree);

        Assert.Single(ids);
    }

    [Fact]
    public void GetSelectedTotalSizeBytes_SumsOnlyCheckedDistinctItems()
    {
        var items = new[]
        {
            MakeItem("1", "A", "X", size: 100),
            MakeItem("2", "B", "X", size: 200),
        };
        var tree = WindowsUpdateCategoryTreeBuilder.Build(items);
        tree[0].Items[0].IsChecked = true; // только первый

        var total = WindowsUpdateCategoryTreeBuilder.GetSelectedTotalSizeBytes(tree);

        Assert.Equal(100, total);
    }

    [Fact]
    public void GetItemsNeedingEula_OnlySelectedAndUnaccepted()
    {
        var eulaItem = new WindowsUpdateItem
        {
            UpdateId = "1", Title = "Driver", EulaAccepted = false, EulaText = "текст лицензии"
        };
        var noEulaItem = new WindowsUpdateItem { UpdateId = "2", Title = "Patch", EulaAccepted = false, EulaText = "" };
        var acceptedItem = new WindowsUpdateItem
        {
            UpdateId = "3", Title = "Other", EulaAccepted = true, EulaText = "текст"
        };

        var tree = new[]
        {
            new WindowsUpdateCategoryNode
            {
                Name = "X",
                Items =
                {
                    new() { Item = eulaItem, IsChecked = true },
                    new() { Item = noEulaItem, IsChecked = true },
                    new() { Item = acceptedItem, IsChecked = true },
                    new() { Item = new WindowsUpdateItem { UpdateId = "4", EulaText = "текст" }, IsChecked = false }, // не выбран
                }
            }
        };

        var result = WindowsUpdateCategoryTreeBuilder.GetItemsNeedingEula(tree);

        Assert.Single(result);
        Assert.Equal("1", result[0].UpdateId);
    }
}
