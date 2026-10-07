using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace Ven4Tools.Views
{
    /// <summary>
    /// ItemsControl, который отдаёт средствам автоматизации содержимое строк напрямую.
    ///
    /// Обычный ItemsControl оборачивает каждую строку в собственный узел (DataItem), а
    /// его содержимое ищет через контейнер строки. После того как список убрали из окна
    /// и вернули (смена вкладки, смена вида каталога), эти узлы остаются пустыми: кнопки
    /// строк видны на экране, но для программ чтения с экрана их больше нет — поймано
    /// UI-тестом на панели «Ваш набор». Здесь узел списка строится обычным обходом
    /// дерева элементов, как у любой панели, и содержимое не теряется.
    ///
    /// Список с группами (GroupStyle) так строить нельзя. Узел группы в WPF
    /// (GroupItemAutomationPeer) свои строки сам не ищет: он берёт их у узла списка и
    /// только если тот — наследник ItemsControlAutomationPeer; с любым другим узлом группа
    /// возвращает пустоту. Упрощённый узел поэтому «терял» все строки сгруппированного
    /// списка — так вышло с двумя главными списками каталога: на экране всё на месте, а
    /// для автоматизации и экранного диктора в категориях пусто. Сгруппированный список
    /// получает узел того же устройства, что у обычного ItemsControl.
    /// </summary>
    public sealed class FlatItemsControl : ItemsControl
    {
        // base.OnCreateAutomationPeer() для сгруппированного списка не годится: штатный узел
        // ItemsControl — внутренний класс WPF, и вызов базового метода из наследника
        // возвращает null (список остаётся вовсе без узла, группы — снова без строк).
        protected override AutomationPeer OnCreateAutomationPeer() =>
            GroupStyle.Count > 0 || IsGrouping ? new GroupedPeer(this) : new FlatPeer(this);

        private sealed class FlatPeer : FrameworkElementAutomationPeer
        {
            public FlatPeer(FlatItemsControl owner) : base(owner) { }

            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.List;

            protected override string GetClassNameCore() => nameof(ItemsControl);
        }

        // Повторяет штатный узел ItemsControl (ItemsControlWrapperAutomationPeer): тот же тип,
        // то же имя класса, строки — узлы DataItem.
        private sealed class GroupedPeer : ItemsControlAutomationPeer
        {
            public GroupedPeer(FlatItemsControl owner) : base(owner) { }

            protected override ItemAutomationPeer CreateItemAutomationPeer(object item) => new RowPeer(item, this);

            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.List;

            protected override string GetClassNameCore() => nameof(ItemsControl);
        }

        private sealed class RowPeer : ItemAutomationPeer
        {
            public RowPeer(object item, ItemsControlAutomationPeer owner) : base(item, owner) { }

            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.DataItem;

            protected override string GetClassNameCore() => "ItemsControlItem";
        }
    }
}
