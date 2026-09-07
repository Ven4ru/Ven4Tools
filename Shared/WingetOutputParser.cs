using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Ven4Tools.Shared
{
    /// <summary>
    /// Разбор текстового вывода winget: очистка от ANSI-последовательностей и
    /// распознавание строк таблицы. Общий для клиента (<c>WingetRunner</c> и все
    /// парсеры, которые им пользуются) и лаунчера (<c>UpdateBackgroundService</c>).
    /// До раунда 39 существовал как две независимые байт-идентичные копии, и это
    /// уже приводило к реальному дефекту: ужесточение ANSI-шаблона попало только в
    /// клиентскую копию, из-за чего в выводе лаунчера оставался мусор, строка-
    /// разделитель «---» не находилась, счётчик обновлений возвращал 0 и лаунчер
    /// молча показывал «обновлений нет» (исправлено в 4.5.0).
    /// </summary>
    internal static class WingetOutputParser
    {
        // [0-9;?]* — параметры CSI включая private-mode '?'; lLM — cursor hide/show.
        // OSC закрывается либо BEL (\x07), либо ST (ESC \), а тело OSC не может
        // проглотить ESC — иначе последовательность смены заголовка окна, которую
        // winget шлёт при прогрессе, съедала весь остаток вывода до следующего BEL
        // вместе со строками таблицы. C1 CSI (\x9B) — однобайтовый вариант ESC[ в
        // наборе C1, второй способ закодировать то же самое, тоже вырезаем.
        private static readonly Regex _ansiRegex =
            new(@"\x1B(?:\[[0-9;?]*[mGKHFABCDsuJhlLM]|\][^\x07\x1B]*(?:\x07|\x1B\\)|[()][0-9A-Za-z])|\x9B[0-9;?]*[mGKHFABCDsuJhlLM]",
                RegexOptions.Compiled);

        /// <summary>Убирает ANSI escape-коды из вывода winget перед разбором.</summary>
        public static string StripAnsi(string s) => _ansiRegex.Replace(s, "");

        /// <summary>
        /// Строка-разделитель таблицы winget: под строкой заголовка колонок winget
        /// печатает строку из дефисов (с пробелами между колонками). Единый строгий
        /// критерий для всех парсеров вывода winget: непустая строка только из дефисов
        /// и пробелов, минимум с одним дефисом. Стро́же прежнего line.Contains("--"),
        /// который ложно срабатывал на строках данных с двойным дефисом.
        /// </summary>
        public static bool IsTableSeparator(string line)
        {
            string t = line.Trim();
            return t.Length > 0 && t.Contains('-') && t.All(c => c == '-' || c == ' ');
        }

        // Внутренний разрыв в 2+ пробела — признак выравнивания колонок таблицы winget.
        private static readonly Regex _columnGapRegex =
            new(@"\S {2,}\S", RegexOptions.Compiled);

        /// <summary>
        /// Строка данных таблицы winget (в отличие от строки-суммарника футера).
        /// Единый локаленезависимый критерий для парсеров вывода winget: любая строка
        /// таблицы содержит хотя бы один внутренний разрыв в 2 и более пробела, которым
        /// winget выравнивает колонки, а футер — одно предложение без таких разрывов.
        ///
        /// Прежние парсеры отсекали футер по английским словам («N upgrades available»),
        /// хотя проект принципиально не передаёт --locale en-US: на русской Windows
        /// футер «Доступны обновления: 32.» под шаблон не подходил и попадал в таблицу
        /// как ещё одна строка обновления, завышая счётчик.
        /// </summary>
        public static bool IsTableRow(string line) => _columnGapRegex.IsMatch(line.Trim());

        /// <summary>
        /// Извлекает строки таблицы «winget upgrade» (между разделителем «---» и
        /// футером-суммарником) из сырого вывода. Раньше эта петля — не сами
        /// примитивы StripAnsi/IsTableSeparator/IsTableRow, а именно цикл, который ими
        /// пользуется — была продублирована ТРИЖДЫ: SystemViewModel.AppUpdates
        /// (клиент, единственная с юнит-тестами), UpdateBackgroundService клиента и
        /// UpdateBackgroundService лаунчера. Три места, где при следующем ужесточении
        /// критерия (как уже случилось однажды с ANSI-шаблоном в раунде 39) можно
        /// снова поправить не все копии разом.
        /// </summary>
        public static List<string> ParseUpgradeTableRows(string rawOutput)
        {
            var rows = new List<string>();
            if (string.IsNullOrWhiteSpace(rawOutput)) return rows;

            var lines = StripAnsi(rawOutput).Replace("\r", "").Split('\n');
            int sepIdx = Array.FindIndex(lines, IsTableSeparator);
            if (sepIdx < 0) return rows;

            for (int i = sepIdx + 1; i < lines.Length; i++)
            {
                string line = lines[i];
                if (string.IsNullOrWhiteSpace(line)) break;
                if (IsTableSeparator(line)) continue;
                if (!IsTableRow(line)) break;
                rows.Add(line.Trim());
            }
            return rows;
        }
    }
}
