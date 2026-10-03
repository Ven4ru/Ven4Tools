using System;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;

namespace Ven4Tools.ClientUITests
{
    /// <summary>
    /// Поиск пункта бокового меню, одинаковый для обеих оболочек главного окна.
    ///
    /// В прежней оболочке все разделы — отдельные пункты и видны сразу. В новой часть
    /// разделов лежит в группах «Windows» и «Сервис», и пока группа свёрнута, её
    /// пунктов нет в дереве автоматизации. Весь набор тестов прогоняется в обеих
    /// оболочках, поэтому тесты ищут пункт меню только через этот помощник: он, если
    /// нужно, раскрывает группу — так же, как это сделал бы пользователь.
    /// </summary>
    public static class UiNav
    {
        private static readonly string[] GroupHeaders = { "btnGroupWindows", "btnGroupService" };

        /// <summary>
        /// Пункт меню по AutomationId или <c>null</c>, если его нет ни в одной оболочке
        /// (например, сетевой раздел скрыт без интернета).
        /// </summary>
        public static AutomationElement? Find(AppSession session, string automationId)
        {
            var element = Lookup(session, automationId);
            if (element != null) return element;

            foreach (string headerId in GroupHeaders)
            {
                var header = Lookup(session, headerId);
                if (header == null) continue; // прежняя оболочка: групп нет

                header.AsButton().Invoke();
                element = Retry.WhileNull(() => Lookup(session, automationId),
                    timeout: TimeSpan.FromSeconds(2), interval: TimeSpan.FromMilliseconds(150),
                    throwOnTimeout: false).Result;
                if (element != null) return element;
            }

            return null;
        }

        private static AutomationElement? Lookup(AppSession session, string automationId) =>
            session.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
    }
}
