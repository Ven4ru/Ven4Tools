using System;
using System.Collections.Generic;
using System.Linq;

namespace Ven4Tools.Services.WindowsUpdate
{
    /// <summary>
    /// Подсказки к скрытию обновлений: какие патчи скрывать опасно (безопасность,
    /// накопительные, базы Защитника) и какие из выбранных — драйверы.
    /// <para>
    /// Скрывать обновление стоит точечно: типичный случай — Windows Update раз за разом
    /// возвращает старый драйвер видеокарты поверх нового. Скрытый патч безопасности
    /// оставляет компьютер без исправления, поэтому перед его скрытием показывается
    /// отдельное предупреждение.
    /// </para>
    /// <para>
    /// Названия категорий Windows Update приходят на языке системы, поэтому сверка идёт
    /// по английским и русским названиям; MsrcSeverity заполнена только у обновлений
    /// безопасности и от языка не зависит.
    /// </para>
    /// </summary>
    public static class WindowsUpdateHideAdvisor
    {
        private static readonly string[] ImportantCategories =
        {
            "Security Updates", "Critical Updates", "Update Rollups", "Definition Updates",
            "Обновления безопасности", "Критические обновления", "Накопительные пакеты обновлений",
            "Обновления определений"
        };

        private static readonly string[] ImportantTitleMarkers =
        {
            "Cumulative Update", "Security Update", "Security Intelligence Update",
            "Накопительное обновление", "Обновление системы безопасности",
            "Обновление для системы безопасности", "Обновление аналитики безопасности"
        };

        private static readonly string[] DriverCategories = { "Drivers", "Драйверы" };

        /// <summary>
        /// Патч, без которого компьютер остаётся без исправлений безопасности или без
        /// накопительного пакета, — перед скрытием нужно предупреждение.
        /// </summary>
        public static bool IsImportant(WindowsUpdateItem item)
        {
            if (!string.IsNullOrWhiteSpace(item.Severity)) return true;
            if (item.CategoryNames.Any(c => ImportantCategories.Any(k =>
                    string.Equals(c?.Trim(), k, StringComparison.OrdinalIgnoreCase))))
                return true;
            return ImportantTitleMarkers.Any(m =>
                item.Title.Contains(m, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Патч из категории «Драйверы».</summary>
        public static bool IsDriver(WindowsUpdateItem item) =>
            item.CategoryNames.Any(c => DriverCategories.Any(k =>
                string.Equals(c?.Trim(), k, StringComparison.OrdinalIgnoreCase)));

        /// <summary>Важные патчи среди выбранных — по одному на UpdateId, в исходном порядке.</summary>
        public static IReadOnlyList<WindowsUpdateItem> SelectImportant(IEnumerable<WindowsUpdateItem> items) =>
            items.Where(IsImportant).DistinctBy(i => i.UpdateId).ToList();
    }
}
