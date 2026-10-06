using System;
using System.Collections.Generic;
using System.Linq;

namespace Ven4Tools.Services
{
    /// <summary>
    /// Порядок установки набора.
    ///
    /// Программы ставятся в том порядке, в каком их выбрали (или назвали в файле
    /// ответа), — раньше порядок зависел от того, какая задача первой дождётся общей
    /// блокировки установки, и от запуска к запуску менялся. Исключение одно: то, что
    /// почти всегда просит перезагрузку (драйверы видеокарт и утилиты для них), уходит в
    /// конец, чтобы просьба о перезагрузке не оказалась посреди набора.
    /// </summary>
    public static class InstallOrder
    {
        // Категория каталога, программы которой ставят или меняют драйверы.
        private const string RebootProneCategory = "Драйверпаки";

        /// <param name="preferredOrder">
        /// Идентификаторы в желаемом порядке (файл ответа, порядок отметок); те, кого в
        /// списке нет, идут после названных, сохраняя взаимный порядок.
        /// </param>
        public static IReadOnlyList<T> Arrange<T>(
            IReadOnlyList<T> items, Func<T, string> idOf, Func<T, string?> categoryOf,
            IReadOnlyList<string>? preferredOrder = null)
        {
            var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (preferredOrder != null)
            {
                for (int i = 0; i < preferredOrder.Count; i++)
                    rank.TryAdd(preferredOrder[i], i);
            }

            return items
                .Select((item, index) => (item, index))
                .OrderBy(x => IsRebootProne(categoryOf(x.item)) ? 1 : 0)
                .ThenBy(x => rank.TryGetValue(idOf(x.item), out int position) ? position : int.MaxValue)
                .ThenBy(x => x.index)
                .Select(x => x.item)
                .ToList();
        }

        public static bool IsRebootProne(string? category) =>
            string.Equals(category?.Trim(), RebootProneCategory, StringComparison.OrdinalIgnoreCase);
    }
}
