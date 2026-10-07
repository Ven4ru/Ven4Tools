using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Ven4Tools.Services
{
    /// <summary>Что powercfg сообщил о настройке «Режим усиления производительности процессора».</summary>
    /// <param name="SchemeGuid">Активная схема электропитания; null — не удалось разобрать.</param>
    /// <param name="Possible">Режимы, которые знает эта Windows (у старых сборок их меньше семи).</param>
    /// <param name="Ac">Текущий режим при питании от сети; null — не удалось разобрать.</param>
    /// <param name="Dc">Текущий режим при питании от батареи.</param>
    public sealed record TurboBoostState(string? SchemeGuid, IReadOnlyList<int> Possible, int? Ac, int? Dc);

    /// <summary>
    /// Режимы усиления производительности процессора (настройка схемы электропитания
    /// PERFBOOSTMODE): названия, описания и разбор вывода powercfg. Чистая логика без
    /// запуска процессов — покрыта тестами.
    /// </summary>
    public static class TurboBoostModes
    {
        public const string Subgroup = "54533251-82be-4824-96c1-47b60b740d00";
        public const string Setting = "be337238-0d82-4146-a960-4f3749d470c7";

        /// <summary>Режимы, описанные в документации Microsoft; запасной список, если powercfg свой не отдал.</summary>
        private static readonly int[] DocumentedModes = { 0, 1, 2, 3, 4 };

        /// <summary>Название режима — как в «Электропитании» Windows.</summary>
        public static string Name(int mode) => mode switch
        {
            0 => "Отключён",
            1 => "Включён",
            2 => "Агрессивный",
            3 => "Эффективно включённый",
            4 => "Эффективно агрессивный",
            5 => "Агрессивный при гарантированном",
            6 => "Эффективно агрессивный при гарантированном",
            _ => $"Режим {mode}"
        };

        /// <summary>
        /// Что режим значит. Формулировки — по документации Microsoft (PERFBOOSTMODE):
        /// там описаны режимы 0–4, про 5 и 6 сказано только название.
        /// </summary>
        public static string Description(int mode) => mode switch
        {
            0 => "Процессор не поднимает частоту выше номинальной: меньше нагрев и шум, ниже скорость.",
            1 => "Процессор поднимает частоту выше номинальной, когда нагрузке это нужно.",
            2 => "Как «Включён», но на процессорах без собственного управления частотой Windows сразу запрашивает наибольшую.",
            3 => "По документации Microsoft работает как «Включён».",
            4 => "По документации Microsoft работает как «Агрессивный».",
            5 or 6 => "Режим есть в Windows, но в документации Microsoft не описан.",
            _ => string.Empty
        };

        /// <summary>
        /// Разбирает вывод <c>powercfg /qh SCHEME_CURRENT &lt;подгруппа&gt; &lt;настройка&gt;</c>.
        /// Подписи строк powercfg переводит на язык Windows, поэтому разбор идёт по форме
        /// значений: первый GUID — схема, строки с тремя цифрами на конце — возможные
        /// режимы, два значения «0x…» — текущие режимы от сети и от батареи.
        /// </summary>
        public static TurboBoostState Parse(string? output)
        {
            output ??= string.Empty;

            var scheme = Regex.Match(output, @"[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}");
            var possible = Regex.Matches(output, @":\s*(\d{3})\s*$", RegexOptions.Multiline)
                .Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
                .Distinct()
                .OrderBy(v => v)
                .ToList();
            var current = Regex.Matches(output, @"0x([0-9a-fA-F]{8})")
                .Select(m => (int?)Convert.ToInt32(m.Groups[1].Value, 16))
                .ToList();

            return new TurboBoostState(
                scheme.Success ? scheme.Value.ToLowerInvariant() : null,
                possible.Count > 0 ? possible : DocumentedModes,
                current.Count > 0 ? current[0] : null,
                current.Count > 1 ? current[1] : null);
        }

        /// <summary>
        /// Подпись режима в списке выбора. Пометки «текущий» и «по умолчанию в Windows»
        /// показывают, от чего человек уходит и к чему может вернуться. Слова передаются
        /// уже на языке интерфейса — так подпись собирается одинаково для обоих языков.
        /// </summary>
        public static string Label(string name, bool isCurrent, bool isDefault, string currentWord, string defaultWord)
        {
            var marks = new List<string>(2);
            if (isCurrent) marks.Add(currentWord);
            if (isDefault) marks.Add(defaultWord);
            return marks.Count == 0 ? name : $"{name} — {string.Join(", ", marks)}";
        }
    }
}
