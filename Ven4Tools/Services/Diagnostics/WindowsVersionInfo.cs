using System;
using System.Collections.Generic;

namespace Ven4Tools.Services.Diagnostics
{
    /// <summary>Сведения о самой Windows для блока «Ваша Windows».</summary>
    internal sealed class WindowsVersionInfo
    {
        public required string ProductName { get; init; }
        public required string DisplayVersion { get; init; }
        public required int Build { get; init; }
        public required int Ubr { get; init; }
        public DateTime? LastCumulativeUpdate { get; init; }
        public string? LastCumulativeUpdateTitle { get; init; }

        public bool IsWindows11 => Build >= Win11FirstBuild;

        internal const int Win11FirstBuild = 22000;
        internal const int Win10_22H2Build = 19045;

        /// <summary>
        /// В реестре у Windows 11 ProductName до сих пор «Windows 10 …» (так Microsoft оставила
        /// для совместимости) — настоящую версию определяем по номеру сборки.
        /// </summary>
        internal static string FixProductName(string? productName, int build)
        {
            string name = string.IsNullOrWhiteSpace(productName) ? "Windows" : productName.Trim();
            if (build >= Win11FirstBuild && name.StartsWith("Windows 10", StringComparison.OrdinalIgnoreCase))
                name = "Windows 11" + name["Windows 10".Length..];
            return name;
        }

        /// <summary>
        /// Понятная строка версии для «Информации о системе» и отчётов: «Windows 11 Pro 25H2
        /// (сборка 26200.8037)» вместо «Microsoft Windows NT 10.0.26200.0» из Environment.OSVersion,
        /// по которой ни выпуск, ни версию, ни номер обновления не узнать.
        /// </summary>
        internal static string ReadCurrentLabel()
        {
            string productName = "", displayVersion = "";
            int build = 0, ubr = 0;
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                if (key != null)
                {
                    productName = key.GetValue("ProductName")?.ToString() ?? "";
                    displayVersion = key.GetValue("DisplayVersion")?.ToString() ?? key.GetValue("ReleaseId")?.ToString() ?? "";
                    int.TryParse(key.GetValue("CurrentBuildNumber")?.ToString() ?? key.GetValue("CurrentBuild")?.ToString(), out build);
                    if (key.GetValue("UBR") is int u) ubr = u;
                }
            }
            catch
            {
                // Реестр недоступен — ниже вернём хотя бы строку .NET.
            }
            if (build == 0) return Environment.OSVersion.VersionString;
            return FormatLabel(FixProductName(productName, build), displayVersion, build, ubr);
        }

        internal static string FormatLabel(string productName, string? displayVersion, int build, int ubr)
        {
            string head = string.IsNullOrWhiteSpace(displayVersion) ? productName : $"{productName} {displayVersion.Trim()}";
            return ubr > 0 ? $"{head} (сборка {build}.{ubr})" : $"{head} (сборка {build})";
        }

        /// <summary>
        /// Заголовок из журнала Windows Update — накопительное обновление самой Windows?
        /// «2026-10 Cumulative Update for Windows 11, version 25H2 …», «… Накопительное
        /// обновление для Windows 10 Version 22H2 …», в том числе Preview. Накопительные
        /// обновления .NET Framework тоже называются «Cumulative Update … for Windows» — их отсекаем.
        /// </summary>
        internal static bool IsWindowsCumulativeUpdateTitle(string? title)
        {
            if (string.IsNullOrWhiteSpace(title)) return false;
            if (title.Contains(".NET", StringComparison.OrdinalIgnoreCase)) return false;
            bool cumulative = title.Contains("Cumulative Update", StringComparison.OrdinalIgnoreCase)
                              || title.Contains("Накопительное обновление", StringComparison.OrdinalIgnoreCase);
            bool forWindows = title.Contains("Windows 1", StringComparison.OrdinalIgnoreCase)
                              || title.Contains("Microsoft server operating system", StringComparison.OrdinalIgnoreCase);
            return cumulative && forWindows;
        }

        /// <summary>
        /// Пояснения к версии: поддержка Windows 10, давность обновлений. Даты — по данным
        /// Microsoft: основная поддержка Windows 10 закончилась 14.10.2025, расширенные
        /// обновления безопасности (ESU) для домашних ПК продлены до 12.10.2027.
        /// Работает ли запись в ESU из России, мы не проверяли — и не обещаем.
        /// </summary>
        internal static List<string> BuildNotes(WindowsVersionInfo info, DateTime today)
        {
            var notes = new List<string>();
            if (!info.IsWindows11)
            {
                if (info.Build == Win10_22H2Build)
                {
                    notes.Add("Обычная поддержка Windows 10 закончилась 14 октября 2025 года. Microsoft продлила для домашних ПК расширенные обновления безопасности (ESU) до 12 октября 2027 года.");
                    notes.Add("Чтобы их получать, ПК нужно записать в программу ESU, а для записи нужна учётная запись Microsoft: Параметры → Обновление и безопасность → Центр обновления Windows → «Записаться». Работает ли запись из России, мы не проверяли и обещать не можем.");
                }
                // LTSC/LTSB (19044 — LTSC 2021, 17763 — LTSC 2019) живут на своих сборках и
                // получают обновления безопасности годами — «обновитесь до 22H2» к ним неверно.
                else if (info.Build > 0 && info.Build < Win10_22H2Build
                         && !info.ProductName.Contains("LTSC", StringComparison.OrdinalIgnoreCase)
                         && !info.ProductName.Contains("LTSB", StringComparison.OrdinalIgnoreCase))
                {
                    notes.Add("Эта версия Windows 10 уже не получает обновлений безопасности. Последняя версия Windows 10 — 22H2: обновитесь до неё через Центр обновления Windows.");
                }
            }

            if (info.LastCumulativeUpdate is DateTime last && (today.Date - last.Date).TotalDays > 60)
                notes.Add("Накопительные обновления не ставились больше двух месяцев — проверьте Центр обновления Windows.");
            return notes;
        }
    }
}
