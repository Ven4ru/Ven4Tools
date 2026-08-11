namespace Ven4Tools.Helpers
{
    /// <summary>
    /// Форматирование байтов в двоичные МБ/ГБ (1024-based) — так их показывает
    /// Windows Explorer и большинство мест этого проекта. Раньше один и тот же
    /// расчёт был переизобретён отдельно в WindowsUpdateTab, CatalogViewModel.Disks.cs
    /// (дважды) и DiagnosticsTab.SystemInfo.cs.
    ///
    /// НЕ трогает DiskBenchmark/BenchmarkReportBuilder.FormatCapacity/FormatBinarySize —
    /// та пара НЕ дублирует это: FormatCapacity намеренно десятичная (ГБ/ТБ, "так её
    /// указывают производители дисков"), FormatBinarySize намеренно подписана "ГиБ",
    /// а не "ГБ" — оба различия там осознанные и точные, унифицировать с этим файлом
    /// значило бы сделать хуже, не лучше.
    /// </summary>
    internal static class SizeFormatter
    {
        /// <summary>Байты → МБ, 1 знак после запятой. "0 МБ" при значении &lt;= 0.</summary>
        public static string BytesToMB(long bytes) =>
            bytes <= 0 ? "0 МБ" : $"{bytes / 1024.0 / 1024.0:F1} МБ";

        /// <summary>Байты → ГБ, целое число (обрезание вниз, как у DriveInfo-отображений).</summary>
        public static string BytesToGBWhole(long bytes) => $"{bytes / 1024 / 1024 / 1024} ГБ";

        /// <summary>Байты → МБ, целое число.</summary>
        public static string BytesToMBWhole(long bytes) => $"{bytes / 1024 / 1024} МБ";
    }
}
