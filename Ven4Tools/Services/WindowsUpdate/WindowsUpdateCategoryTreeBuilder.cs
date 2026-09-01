using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Ven4Tools.Helpers;

namespace Ven4Tools.Services.WindowsUpdate
{
    public sealed class WindowsUpdateItemNode : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public WindowsUpdateItem Item { get; init; } = null!;

        private bool _isChecked;
        public bool IsChecked
        {
            get => _isChecked;
            set
            {
                if (_isChecked == value) return;
                _isChecked = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
            }
        }

        /// <summary>
        /// Текст строки патча для UI — раньше собирался в WindowsUpdateTab.RenderTree().
        /// Хвост «✅ Скачан» показывается для патчей, которые Windows Update уже положил
        /// в кэш (сам, по расписанию системы, или наш фоновый режим «Уведомлять и
        /// скачивать в фоне»): их установка начнётся сразу, без ожидания загрузки, и
        /// заявленный размер уже не будет качаться из сети. Формат «| статус» — тот же,
        /// что у строк каталога (AppRowViewModel.StatusTooltip).
        /// </summary>
        public string DisplayText =>
            $"{Item.Title}" +
            (Item.KbArticleIds.Count > 0 ? $" (KB{string.Join(", KB", Item.KbArticleIds)})" : "") +
            $" — {SizeFormatter.BytesToMB(Item.SizeBytes)}" +
            (Item.IsDownloaded ? " | ✅ Скачан, готов к установке" : "");
    }

    public sealed class WindowsUpdateCategoryNode : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public string Name { get; init; } = "";
        public List<WindowsUpdateItemNode> Items { get; init; } = new();

        // null = частично выбрано (tri-state), true = все выбраны, false = ни одного.
        private bool? _isChecked = false;
        public bool? IsChecked
        {
            get => _isChecked;
            set
            {
                if (_isChecked == value) return;
                _isChecked = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
            }
        }

        /// <summary>Текст заголовка категории для UI — раньше собирался в WindowsUpdateTab.RenderTree().</summary>
        public string HeaderText => $"{Name} ({Items.Count})";
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

        /// <summary>
        /// Патчи среди выбранных, у которых есть непринятый EULA — их текст нужно
        /// показать в диалоге подтверждения перед стартом установки. Раньше жил в
        /// WindowsUpdateErrorMapper (маппер кодов ошибок) — работает над тем же
        /// деревом, что и GetSelectedUpdateIds/GetSelectedTotalSizeBytes выше, к
        /// расшифровке кодов ошибок отношения не имеет.
        /// </summary>
        public static IReadOnlyList<WindowsUpdateItem> GetItemsNeedingEula(
            IReadOnlyList<WindowsUpdateCategoryNode> tree)
        {
            return tree
                .SelectMany(c => c.Items)
                .Where(i => i.IsChecked)
                .Select(i => i.Item)
                .Where(item => !item.EulaAccepted && !string.IsNullOrWhiteSpace(item.EulaText))
                .DistinctBy(item => item.UpdateId)
                .ToList();
        }
    }
}
