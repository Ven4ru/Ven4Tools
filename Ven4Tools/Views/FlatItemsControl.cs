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
    /// </summary>
    public sealed class FlatItemsControl : ItemsControl
    {
        protected override AutomationPeer OnCreateAutomationPeer() => new FlatPeer(this);

        private sealed class FlatPeer : FrameworkElementAutomationPeer
        {
            public FlatPeer(FlatItemsControl owner) : base(owner) { }

            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.List;

            protected override string GetClassNameCore() => nameof(ItemsControl);
        }
    }
}
