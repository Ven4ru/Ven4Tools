using Ven4Tools.Services.WindowsUpdate;

namespace Ven4Tools.Tests;

/// <summary>
/// Один патч под несколькими категориями — несколько узлов дерева с одним общим
/// выбором: снятая в одной категории галка не должна оставлять патч выбранным в другой.
/// </summary>
public sealed class S2WindowsUpdateTreeSyncTests
{
    private static IReadOnlyList<WindowsUpdateCategoryNode> BuildTwoCategoryTree() =>
        WindowsUpdateCategoryTreeBuilder.Build(new[]
        {
            new WindowsUpdateItem
            {
                UpdateId = "1", Title = "A", CategoryNames = new[] { "Security Updates", "Critical Updates" }
            },
            new WindowsUpdateItem
            {
                UpdateId = "2", Title = "B", CategoryNames = new[] { "Security Updates" }
            }
        });

    [Fact]
    public void СнятиеВОднойКатегории_СнимаетПатчИВДругой()
    {
        var tree = BuildTwoCategoryTree();
        foreach (var category in tree) WindowsUpdateCategoryTreeBuilder.ApplyCategoryCheck(category, true);

        var security = tree.First(c => c.Name == "Security Updates");
        security.Items.First(i => i.Item.UpdateId == "1").IsChecked = false;

        var critical = tree.First(c => c.Name == "Critical Updates");
        Assert.False(critical.Items.Single().IsChecked);
        Assert.Equal("2", Assert.Single(WindowsUpdateCategoryTreeBuilder.GetSelectedUpdateIds(tree)));
    }

    [Fact]
    public void ОтметкаКатегории_ОтмечаетПатчВоВсехЕгоКатегориях()
    {
        var tree = BuildTwoCategoryTree();

        WindowsUpdateCategoryTreeBuilder.ApplyCategoryCheck(tree.First(c => c.Name == "Critical Updates"), true);

        var security = tree.First(c => c.Name == "Security Updates");
        Assert.True(security.Items.First(i => i.Item.UpdateId == "1").IsChecked);
        Assert.False(security.Items.First(i => i.Item.UpdateId == "2").IsChecked);
    }
}
