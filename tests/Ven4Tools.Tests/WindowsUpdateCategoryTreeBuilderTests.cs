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
        var item = MakeItem("1", "A", "Security Updates");
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
}
