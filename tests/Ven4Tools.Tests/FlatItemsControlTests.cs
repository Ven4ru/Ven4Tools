using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;
using Ven4Tools.Views;

namespace Ven4Tools.Tests;

/// <summary>
/// Упрощённый список (FlatItemsControl) и средства автоматизации: строки должны быть
/// видны экранному диктору и проверкам интерфейса и в простом списке, и в списке с группами.
/// </summary>
public class FlatItemsControlTests
{
    private sealed record Row(string Name, string Category);

    private static readonly Row[] Rows =
    {
        new("Firefox", "Браузеры"), new("Chrome", "Браузеры"), new("7-Zip", "Системные"),
    };

    private static readonly string[] AllNames = { "7-Zip", "Chrome", "Firefox" };

    // Элементы WPF живут только в потоке с однопоточной моделью (STA).
    private static void OnUiThread(Action body)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();
        failure?.Throw();
    }

    private static T Prepare<T>(T list, bool grouped) where T : ItemsControl
    {
        // Без окна стиль и шаблон элемент получает только после инициализации.
        list.BeginInit();
        var template = new DataTemplate();
        var button = new FrameworkElementFactory(typeof(Button));
        button.SetBinding(ContentControl.ContentProperty, new Binding(nameof(Row.Name)));
        template.VisualTree = button;
        list.ItemTemplate = template;

        var view = new ListCollectionView(Rows.ToList());
        if (grouped)
        {
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Row.Category)));
            list.GroupStyle.Add(new GroupStyle());
        }
        list.ItemsSource = view;
        list.EndInit();

        list.Measure(new Size(600, 600));
        list.Arrange(new Rect(0, 0, 600, 600));
        list.UpdateLayout();
        return list;
    }

    private static List<AutomationPeer> ChildrenOf(AutomationPeer peer) => peer.GetChildren() ?? new List<AutomationPeer>();

    private static IEnumerable<AutomationPeer> Descendants(AutomationPeer peer)
    {
        foreach (var child in ChildrenOf(peer))
        {
            yield return child;
            foreach (var deeper in Descendants(child)) yield return deeper;
        }
    }

    private static List<AutomationPeer> GroupsOf(AutomationPeer listPeer) => ChildrenOf(listPeer)
        .Where(p => p.GetAutomationControlType() == AutomationControlType.Group)
        .ToList();

    private static List<string> ButtonNames(AutomationPeer listPeer) => Descendants(listPeer)
        .Where(p => p.GetAutomationControlType() == AutomationControlType.Button)
        .Select(p => p.GetName())
        .OrderBy(n => n, StringComparer.Ordinal)
        .ToList();

    [Fact]
    public void PlainList_ExposesRowContentDirectly()
    {
        OnUiThread(() =>
        {
            var list = Prepare(new FlatItemsControl(), grouped: false);
            var peer = UIElementAutomationPeer.CreatePeerForElement(list);

            Assert.NotNull(peer);
            Assert.False(peer is ItemsControlAutomationPeer);
            Assert.Equal(AutomationControlType.List, peer.GetAutomationControlType());
            // Кнопки строк — прямые потомки списка, без промежуточных узлов DataItem.
            Assert.DoesNotContain(Descendants(peer), p => p.GetAutomationControlType() == AutomationControlType.DataItem);
            Assert.Equal(AllNames, ButtonNames(peer));
        });
    }

    [Fact]
    public void GroupedList_GroupsExposeTheirRows()
    {
        OnUiThread(() =>
        {
            var list = Prepare(new FlatItemsControl(), grouped: true);
            var peer = UIElementAutomationPeer.CreatePeerForElement(list);

            Assert.NotNull(peer);
            Assert.True(peer is ItemsControlAutomationPeer);
            Assert.Equal(2, GroupsOf(peer).Count);
            Assert.Equal(AllNames, ButtonNames(peer));
        });
    }

    [Fact]
    public void GroupedList_LooksToAutomationLikeStandardItemsControl()
    {
        OnUiThread(() =>
        {
            static List<string> Shape(AutomationPeer root) => Descendants(root)
                .Select(p => p.GetAutomationControlType() + ":" + p.GetClassName())
                .ToList();

            var flat = UIElementAutomationPeer.CreatePeerForElement(Prepare(new FlatItemsControl(), grouped: true));
            var standard = UIElementAutomationPeer.CreatePeerForElement(Prepare(new ItemsControl(), grouped: true));

            Assert.Equal(standard.GetAutomationControlType(), flat.GetAutomationControlType());
            Assert.Equal(standard.GetClassName(), flat.GetClassName());
            Assert.Equal(Shape(standard), Shape(flat));
        });
    }

    // Список с тем узлом, какой FlatItemsControl отдавал бы и при группах, не будь у него
    // отдельной ветки для них.
    private sealed class ForcedFlatList : ItemsControl
    {
        protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);
    }

    [Fact]
    public void GroupNode_FindsNoRows_WhenListNodeIsNotItemsControlPeer()
    {
        // Причина, по которой сгруппированный список нельзя переводить на упрощённый узел:
        // узел группы спрашивает строки у узла списка и без ItemsControlAutomationPeer пуст.
        OnUiThread(() =>
        {
            var list = Prepare(new ForcedFlatList(), grouped: true);
            var peer = UIElementAutomationPeer.CreatePeerForElement(list);

            var groups = GroupsOf(peer);
            Assert.Equal(2, groups.Count);
            Assert.All(groups, group => Assert.Empty(ChildrenOf(group)));
            Assert.Empty(ButtonNames(peer));
        });
    }
}
