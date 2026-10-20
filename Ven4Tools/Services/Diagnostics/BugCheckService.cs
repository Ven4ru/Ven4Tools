using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Ven4Tools.Services.Diagnostics
{
    /// <summary>Подробности синего экрана: код, параметры 1–4 и файл дампа.</summary>
    internal sealed class BugCheckDetails
    {
        public required uint Code { get; init; }

        /// <summary>Параметры 1–4 в порядке Windows; пусто, если журнал их не сохранил.</summary>
        public required IReadOnlyList<ulong> Parameters { get; init; }

        /// <summary>Куда Windows сохранила дамп (событие 1001); null — событие не найдено.</summary>
        public string? DumpPath { get; init; }
    }

    /// <summary>Отчёт о синем экране из события 1001 (источник BugCheck / WER-SystemErrorReporting).</summary>
    internal sealed class WerBugCheckReport
    {
        public required DateTime TimeCreated { get; init; }
        public required uint Code { get; init; }
        public required IReadOnlyList<ulong> Parameters { get; init; }
        public string? DumpPath { get; init; }
    }

    /// <summary>Драйвер, поставленный или обновлённый незадолго до сбоя.</summary>
    internal sealed class DriverInstallRecord
    {
        public required DateTime Time { get; init; }
        public required string Name { get; init; }
    }

    /// <summary>Что лежит в папке минидампов.</summary>
    internal sealed class MinidumpFolderInfo
    {
        public required string FolderPath { get; init; }
        public required bool Exists { get; init; }
        public required int FileCount { get; init; }
        public DateTime? LatestFileTime { get; init; }
    }

    /// <summary>
    /// Разбор следов синего экрана в журналах Windows. Только чтение: журналы событий,
    /// setupapi.dev.log и список файлов в папке минидампов. Сами дампы не открываются.
    /// Чистые функции разбора — internal static, их проверяют юнит-тесты.
    /// </summary>
    internal static class BugCheckService
    {
        /// <summary>За сколько дней до первого сбоя ищем поставленные драйверы.</summary>
        internal static readonly TimeSpan DriverLookback = TimeSpan.FromDays(3);

        /// <summary>
        /// Событие 41 (Kernel-Power) и 1001 (BugCheck) пишутся при следующей загрузке, обычно
        /// в пределах пары минут друг от друга; окно с запасом на медленный старт.
        /// </summary>
        internal static readonly TimeSpan ReportCorrelationWindow = TimeSpan.FromMinutes(15);

        private static readonly Regex WerParam1Regex = new(
            @"^\s*(?:0x)?(?<code>[0-9a-fA-F]{1,8})\s*\((?<params>[^)]*)\)",
            RegexOptions.CultureInvariant);

        /// <summary>
        /// Разбор param1 события 1001: «0x0000000a (0xffff..., 0x0000000000000002, 0x..., 0x...)».
        /// null — строка не похожа на код синего экрана.
        /// </summary>
        internal static (uint Code, IReadOnlyList<ulong> Parameters)? ParseWerParam1(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var m = WerParam1Regex.Match(text);
            if (!m.Success) return null;
            if (!uint.TryParse(m.Groups["code"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint code)) return null;

            var parameters = new List<ulong>();
            foreach (string part in m.Groups["params"].Value.Split(','))
            {
                string p = part.Trim();
                if (p.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) p = p[2..];
                if (p.Length == 0) continue;
                if (!ulong.TryParse(p, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong value)) return (code, Array.Empty<ulong>());
                parameters.Add(value);
            }
            return (code, parameters);
        }

        /// <summary>
        /// Параметры из события 41 (Kernel-Power): BugcheckParameter1..4 записаны десятичными
        /// числами (или 0x-строкой на части сборок). Нет хотя бы одного — пусто.
        /// </summary>
        internal static IReadOnlyList<ulong> ParseKernelPowerParameters(IReadOnlyDictionary<string, string> data)
        {
            var list = new List<ulong>(4);
            for (int i = 1; i <= 4; i++)
            {
                if (!data.TryGetValue("BugcheckParameter" + i, out var raw) || !TryParseNumber(raw, out ulong value))
                    return Array.Empty<ulong>();
                list.Add(value);
            }
            return list;
        }

        private static bool TryParseNumber(string? raw, out ulong value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            string s = raw.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return ulong.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
            return ulong.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>Параметры в виде «0x…, 0x…, 0x…, 0x…» (16 знаков — как в журнале 64-битной Windows).</summary>
        internal static string FormatParameters(IReadOnlyList<ulong> parameters) =>
            string.Join(", ", parameters.Select(p => "0x" + p.ToString("X16", CultureInfo.InvariantCulture)));

        /// <summary>
        /// Отчёт 1001, относящийся к событию 41: ближайший по времени в пределах окна, и код
        /// совпадает (если он известен в событии 41). null — подходящего нет.
        /// </summary>
        internal static WerBugCheckReport? FindMatchingReport(DateTime crashTime, uint code, IReadOnlyList<WerBugCheckReport> reports) =>
            reports
                .Where(r => (code == 0 || r.Code == code)
                            && (r.TimeCreated - crashTime).Duration() <= ReportCorrelationWindow)
                .OrderBy(r => (r.TimeCreated - crashTime).Duration())
                .FirstOrDefault();

        /// <summary>Отчёты о синих экранах (событие 1001) начиная с cutoff.</summary>
        public static List<WerBugCheckReport> GetWerReports(DateTime cutoff)
        {
            var list = new List<WerBugCheckReport>();
            foreach (var e in SystemHealthService.QueryEvents("System", "Microsoft-Windows-WER-SystemErrorReporting", 1001, cutoff))
            {
                e.Data.TryGetValue("param1", out var param1);
                var parsed = ParseWerParam1(param1);
                if (parsed == null) continue;
                e.Data.TryGetValue("param2", out var dump);
                list.Add(new WerBugCheckReport
                {
                    TimeCreated = e.TimeCreated,
                    Code = parsed.Value.Code,
                    Parameters = parsed.Value.Parameters,
                    DumpPath = string.IsNullOrWhiteSpace(dump) ? null : dump.Trim()
                });
            }
            return list;
        }

        // ── «Возможная связь»: драйверы, поставленные перед первым сбоем ─────────────────

        /// <summary>
        /// Драйверы, поставленные или обновлённые в окне [from; to]. Три источника:
        /// событие 400 журнала Kernel-PnP/Configuration с DeviceUpdated=true («устройству
        /// назначен другой драйвер» — есть и в Windows 10, и в 11), событие 20001 (UserPnp —
        /// пишет Windows 10, в новых сборках Windows 11 его может не быть) и журнал установки
        /// драйверов setupapi.dev.log (импорт пакета в хранилище — так видны и драйверы,
        /// пришедшие через Windows Update).
        /// </summary>
        public static List<DriverInstallRecord> GetDriverInstalls(DateTime from, DateTime to)
        {
            var result = new List<DriverInstallRecord>();
            try
            {
                foreach (var e in SystemHealthService.QueryEvents("Microsoft-Windows-Kernel-PnP/Configuration", "Microsoft-Windows-Kernel-PnP", 400, from))
                {
                    if (e.TimeCreated > to) continue;
                    string? name = DescribeKernelPnpConfigured(e.Data);
                    if (name != null) result.Add(new DriverInstallRecord { Time = e.TimeCreated, Name = name });
                }
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "BugCheckService.GetDriverInstalls(Kernel-PnP)");
            }

            try
            {
                foreach (var e in SystemHealthService.QueryEvents("System", "Microsoft-Windows-UserPnp", 20001, from))
                {
                    if (e.TimeCreated > to) continue;
                    // Неуспешная установка драйвер не меняла — к связи со сбоем не относится.
                    if (e.Data.TryGetValue("InstallStatus", out var status)
                        && !string.IsNullOrWhiteSpace(status) && status.Trim() is not ("0x0" or "0"))
                        continue;
                    string? name = DescribeUserPnpInstall(e.Data);
                    if (name != null) result.Add(new DriverInstallRecord { Time = e.TimeCreated, Name = name });
                }
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "BugCheckService.GetDriverInstalls(UserPnp)");
            }

            try
            {
                string log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF", "setupapi.dev.log");
                if (File.Exists(log))
                    result.AddRange(ParseSetupApiLog(ReadTail(log, 8 * 1024 * 1024), from, to));
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "BugCheckService.GetDriverInstalls(setupapi)");
            }

            return MergeDriverInstalls(result);
        }

        /// <summary>
        /// Подпись драйвера из события 400 Kernel-PnP/Configuration. null — драйвер устройства не
        /// менялся (DeviceUpdated ≠ true) или это встроенный драйвер Windows (DriverInbox=true):
        /// такие обновляются пачками вместе с самой Windows и только зашумили бы список.
        /// </summary>
        internal static string? DescribeKernelPnpConfigured(IReadOnlyDictionary<string, string> data)
        {
            string? Get(string key) => data.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

            if (!string.Equals(Get("DeviceUpdated"), "true", StringComparison.OrdinalIgnoreCase)) return null;
            if (string.Equals(Get("DriverInbox"), "true", StringComparison.OrdinalIgnoreCase)) return null;

            // DriverPackageId «xvdd.inf_amd64_8f15a2927d7ab02d» хранит исходное имя INF;
            // DriverName — системное «oem146.inf», по которому человеку ничего не понятно.
            string? package = Get("DriverPackageId");
            string? inf = package != null && package.Contains(".inf", StringComparison.OrdinalIgnoreCase)
                ? package[..(package.IndexOf(".inf", StringComparison.OrdinalIgnoreCase) + 4)]
                : Get("DriverName");
            if (inf == null) return null;

            var extra = new List<string>();
            if (Get("DriverProvider") is string provider) extra.Add(provider);
            if (Get("DriverVersion") is string version) extra.Add(version);
            return extra.Count > 0 ? $"{inf} ({string.Join(", ", extra)})" : inf;
        }

        /// <summary>Подпись драйвера из события 20001: описание, поставщик, версия, имя INF.</summary>
        internal static string? DescribeUserPnpInstall(IReadOnlyDictionary<string, string> data)
        {
            string? Get(string key) => data.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

            string? description = Get("DriverDescription");
            string? provider = Get("DriverProvider");
            string? version = Get("DriverVersion");
            string? inf = Get("DriverName");
            if (description == null && inf == null) return null;

            var sb = new StringBuilder(description ?? inf);
            var extra = new List<string>();
            if (provider != null && (description == null || !description.Contains(provider, StringComparison.OrdinalIgnoreCase))) extra.Add(provider);
            if (version != null) extra.Add(version);
            if (description != null && inf != null) extra.Add(Path.GetFileName(inf));
            if (extra.Count > 0) sb.Append(" (").Append(string.Join(", ", extra)).Append(')');
            return sb.ToString();
        }

        private static readonly Regex SetupApiHeaderRegex = new(
            @"^>>>\s+\[(?<kind>Setup Import Driver Package|Device Install \(DiInstallDriver\))\s+-\s+(?<target>.+?)\]\s*$",
            RegexOptions.CultureInvariant);

        private static readonly Regex SetupApiStartRegex = new(
            @"^>>>\s+Section start\s+(?<ts>\d{4}/\d{2}/\d{2} \d{2}:\d{2}:\d{2})",
            RegexOptions.CultureInvariant);

        /// <summary>
        /// Разбор setupapi.dev.log: секции импорта пакета драйвера («Setup Import Driver Package»)
        /// и установки через DiInstallDriver. За заголовком секции следующей строкой идёт
        /// «Section start ГГГГ/ММ/ДД чч:мм:сс». Из пути берётся только имя INF-файла — путь
        /// может лежать во временной папке профиля и содержать имя пользователя.
        /// </summary>
        internal static List<DriverInstallRecord> ParseSetupApiLog(IEnumerable<string> lines, DateTime from, DateTime to)
        {
            var result = new List<DriverInstallRecord>();
            string? pendingTarget = null;
            foreach (string line in lines)
            {
                var header = SetupApiHeaderRegex.Match(line);
                if (header.Success)
                {
                    pendingTarget = header.Groups["target"].Value.Trim();
                    continue;
                }
                if (pendingTarget == null) continue;

                var start = SetupApiStartRegex.Match(line);
                if (start.Success
                    && DateTime.TryParseExact(start.Groups["ts"].Value, "yyyy/MM/dd HH:mm:ss",
                        CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var time)
                    && time >= from && time <= to)
                {
                    string name = InfFileName(pendingTarget);
                    if (name.Length > 0) result.Add(new DriverInstallRecord { Time = time, Name = name });
                }
                pendingTarget = null;
            }
            return result;
        }

        private static string InfFileName(string target)
        {
            try
            {
                string name = Path.GetFileName(target.Trim().Trim('"'));
                return name.EndsWith(".inf", StringComparison.OrdinalIgnoreCase) ? name : "";
            }
            catch (ArgumentException)
            {
                return "";
            }
        }

        /// <summary>
        /// Убрать повторы: один и тот же драйвер бывает в нескольких источниках («xvdd.inf» из
        /// setupapi и «xvdd.inf (Xbox, 10.0…)» из Kernel-PnP) и на нескольких устройствах.
        /// Ключ — имя до скобки; из группы берётся самая подробная подпись и самое позднее время.
        /// Свежие сверху.
        /// </summary>
        internal static List<DriverInstallRecord> MergeDriverInstalls(IEnumerable<DriverInstallRecord> records) =>
            records
                .GroupBy(r => MergeKey(r.Name), StringComparer.OrdinalIgnoreCase)
                .Select(g => new DriverInstallRecord
                {
                    Time = g.Max(r => r.Time),
                    Name = g.OrderByDescending(r => r.Name.Length).First().Name
                })
                .OrderByDescending(r => r.Time)
                .ToList();

        private static string MergeKey(string name)
        {
            int bracket = name.IndexOf(" (", StringComparison.Ordinal);
            return (bracket > 0 ? name[..bracket] : name).Trim();
        }

        /// <summary>Хвост большого текстового журнала (файл открыт на запись системой — читаем с FileShare.ReadWrite).</summary>
        private static List<string> ReadTail(string path, long maxBytes)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length > maxBytes) fs.Seek(-maxBytes, SeekOrigin.End);
            using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var lines = new List<string>();
            string? line;
            while ((line = reader.ReadLine()) != null) lines.Add(line);
            return lines;
        }

        // ── Минидампы ─────────────────────────────────────────────────────────────────

        public static string MinidumpFolder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Minidump");

        public static MinidumpFolderInfo GetMinidumpInfo()
        {
            string folder = MinidumpFolder;
            try
            {
                if (!Directory.Exists(folder))
                    return new MinidumpFolderInfo { FolderPath = folder, Exists = false, FileCount = 0 };
                var files = new DirectoryInfo(folder).GetFiles("*.dmp");
                return new MinidumpFolderInfo
                {
                    FolderPath = folder,
                    Exists = true,
                    FileCount = files.Length,
                    LatestFileTime = files.Length > 0 ? files.Max(f => f.LastWriteTime) : null
                };
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "BugCheckService.GetMinidumpInfo");
                return new MinidumpFolderInfo { FolderPath = folder, Exists = Directory.Exists(folder), FileCount = 0 };
            }
        }
    }
}
