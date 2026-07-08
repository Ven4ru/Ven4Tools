using System.Collections.Generic;
using System.Linq;

namespace Ven4Tools.Services.WindowsUpdate
{
    public sealed class WindowsUpdateItemNode
    {
        public WindowsUpdateItem Item { get; init; } = null!;
        public bool IsChecked { get; set; }
    }

    public sealed class WindowsUpdateCategoryNode
    {
        public string Name { get; init; } = "";
        public List<WindowsUpdateItemNode> Items { get; init; } = new();

        // null = частично выбрано (tri-state), true = все выбраны, false = ни одного.
        public bool? IsChecked { get; set; } = false;
    }

    /// <summary>
    /// Группирует патчи по категориям (IUpdate.Categories может содержать несколько —
    /// патч попадает в дерево под каждой своей категорией, это ожидаемое поведение
    /// Windows Update: например, один патч может быть и "Security Updates", и "Critical Updates").
    /// Категория без имени (в API это возможно для мусорных/служебных категорий) — в "Другое".
    /// </summary>
    public static class WindowsUpdateCategoryTreeBuilder
    {
        private const string Uncategorized = "Другое";

        public static IReadOnlyList<WindowsUpdateCategoryNode> Build(IReadOnlyList<WindowsUpdateItem> items)
        {
            var byCategory = new Dictionary<string, WindowsUpdateCategoryNode>();

            foreach (var item in items)
            {
                var categoryNames = item.CategoryNames.Count > 0
                    ? item.CategoryNames
                    : new[] { Uncategorized };

                foreach (var categoryName in categoryNames)
                {
                    var name = string.IsNullOrWhiteSpace(categoryName) ? Uncategorized : categoryName;
                    if (!byCategory.TryGetValue(name, out var node))
                    {
                        node = new WindowsUpdateCategoryNode { Name = name };
                        byCategory[name] = node;
                    }
                    node.Items.Add(new WindowsUpdateItemNode { Item = item, IsChecked = false });
                }
            }

            return byCategory.Values.OrderBy(n => n.Name).ToList();
        }

        /// <summary>Вызывать после того, как пользователь щёлкнул чекбокс отдельного патча.</summary>
        public static void RecalculateCategoryState(WindowsUpdateCategoryNode category)
        {
            if (category.Items.Count == 0) { category.IsChecked = false; return; }

            bool allChecked = category.Items.All(i => i.IsChecked);
            bool noneChecked = category.Items.All(i => !i.IsChecked);

            category.IsChecked = allChecked ? true : noneChecked ? false : (bool?)null;
        }

        /// <summary>Вызывать после того, как пользователь щёлкнул чекбокс категории.</summary>
        public static void ApplyCategoryCheck(WindowsUpdateCategoryNode category, bool isChecked)
        {
            foreach (var item in category.Items)
                item.IsChecked = isChecked;
            category.IsChecked = isChecked;
        }

        public static IReadOnlyList<string> GetSelectedUpdateIds(IReadOnlyList<WindowsUpdateCategoryNode> tree) =>
            tree.SelectMany(c => c.Items)
                .Where(i => i.IsChecked)
                .Select(i => i.Item.UpdateId)
                .Distinct()
                .ToList();

        public static long GetSelectedTotalSizeBytes(IReadOnlyList<WindowsUpdateCategoryNode> tree) =>
            tree.SelectMany(c => c.Items)
                .Where(i => i.IsChecked)
                .Select(i => i.Item)
                .DistinctBy(i => i.UpdateId)
                .Sum(i => i.SizeBytes);
    }
}
