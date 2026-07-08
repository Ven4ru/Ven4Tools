using System.Collections.Generic;
using System.Linq;

namespace Ven4Tools.Services.WindowsUpdate
{
    public static class WindowsUpdateErrorMapper
    {
        // Известные коды из wuerror.h — самые частые в практике конечных пользователей.
        private static readonly Dictionary<int, string> KnownHResults = new()
        {
            { unchecked((int)0x80240438), "Не удалось подключиться к серверу обновлений (сетевая ошибка)." },
            { unchecked((int)0x8024402C), "Нет соединения с интернетом — проверка обновлений недоступна." },
            { unchecked((int)0x80070422), "Служба Windows Update отключена. Включите её и повторите попытку." },
            { unchecked((int)0x8024001E), "Операция отменена." },
            { unchecked((int)0x80240022), "Патч больше не предлагается сервером обновлений (устарел)." },
            { unchecked((int)0x8007000E), "Недостаточно памяти для выполнения операции." },
            { unchecked((int)0x80070005), "Отказано в доступе — операция требует прав администратора." },
        };

        public static string MapHResult(int hresult) =>
            KnownHResults.TryGetValue(hresult, out var message)
                ? message
                : $"Ошибка Windows Update (код 0x{hresult:X8}). Подробности — в логе.";

        /// <summary>
        /// Патчи среди выбранных, у которых есть непринятый EULA — их текст нужно
        /// показать в диалоге подтверждения перед стартом установки.
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
