using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Ven4Tools.Services
{
    public class InstalledAppsService
    {
        // Разобранный вывод `winget list`: токен (идентификатор пакета) → версия из
        // колонки Version. Раньше здесь хранился сырой текст вывода, и КАЖДЫЙ вызов
        // IsInstalled/GetInstalledVersion разбирал его заново: линейный скан всего
        // вывода, Replace("\r\n","\n") + Split('\n') с аллокацией массива строк и
        // поиском строки-разделителя на каждый вызов. CatalogViewModel вызывает обе
        // проверки в цикле по всем строкам каталога (сотни приложений) на UI-потоке,
        // то есть один и тот же вывод разбирался сотни раз подряд. Теперь разбор
        // происходит ровно один раз — в RefreshAsync/LoadFromOutput, а проверки
        // сводятся к поиску в словаре.
        //
        // Сравнение ключей — OrdinalIgnoreCase: прежний поиск шёл через
        // IndexOf(..., StringComparison.OrdinalIgnoreCase), матчинг ID не меняется.
        private Dictionary<string, string> _installed = new(StringComparer.OrdinalIgnoreCase);

        // Разделители «первого слова» колонки Version — тот же набор, что и раньше,
        // но статический: массив больше не создаётся на каждый разбор строки.
        private static readonly char[] VersionValueSeparators = { ' ', '\t' };

        // Названия колонки версии в заголовке таблицы (нелокализованный и русский
        // варианты на случай локализации winget). Тоже вынесены в статику — раньше
        // массив аллоцировался при каждом вызове GetInstalledVersion.
        private static readonly string[] VersionHeaderKeys = { "Version", "Версия" };

        public async Task RefreshAsync()
        {
            try
            {
                var (_, output) = await WingetRunner.RunAsync(WingetArgs.Query("list"));
                // ANSI-последовательности убираем сразу при получении вывода, а не
                // в каждом разборе отдельно: без этого escape-код перед идентификатором
                // ломал границу «слева пробел» при поиске токена, а при разборе версии
                // сдвигал позицию колонки Version относительно строки заголовка и делал
                // строку-разделитель ненаходимой. Тот же StripAnsi, что уже применяют
                // остальные разборы вывода winget.
                LoadFromOutput(WingetRunner.StripAnsi(output));
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[InstalledAppsService] Получение списка установленных приложений (winget list): {ex.Message}");
                LoadFromOutput(string.Empty);
            }
        }

        /// <summary>
        /// Проверка ОДНОГО идентификатора без выгрузки всего списка установленного:
        /// `winget list --id &lt;id&gt; --exact`. Отдельный метод, а не переиспользование
        /// <see cref="RefreshAsync"/>, именно потому, что смысл другой — не «снять
        /// снимок всей системы», а «есть ли конкретный пакет». Нужен верификации
        /// результата установки (InstallationService.Outcome), которая раньше ради
        /// одного ID гоняла полный `winget list` до четырёх раз на каждое приложение
        /// на строго последовательном критическом пути установки.
        ///
        /// Флаг `--source winget` намеренно НЕ передаётся (в отличие от `winget show`
        /// в AvailabilityChecker/WingetVersionsService): он ограничивает выдачу
        /// пакетами, известными каталогу winget, и отсекает записи ARP/реестра — а
        /// проверять надо результат установки ЛЮБЫМ способом (choco, прямая ссылка,
        /// локальный установщик), которые как раз видны только как записи ARP.
        ///
        /// CommandLineGuard.ValidateId здесь тоже не применяется: аргументы уходят
        /// через ArgumentList (.NET экранирует каждый токен сам, «вырваться» в
        /// посторонний флаг нельзя), а белый список идентификаторов запрещает фигурные
        /// скобки, которые встречаются в реальных ID записей ARP ({GUID} у MSI) —
        /// прежний путь через полный список такие ID находил.
        /// </summary>
        public static async Task<(bool Installed, string Version)> QuerySinglePackageAsync(string wingetId)
        {
            if (string.IsNullOrEmpty(wingetId)) return (false, string.Empty);

            try
            {
                var (_, output) = await WingetRunner.RunAsync(SinglePackageArgs(wingetId));
                var probe = new InstalledAppsService();
                probe.LoadFromOutput(WingetRunner.StripAnsi(output));
                return probe.IsInstalled(wingetId)
                    ? (true, probe.GetInstalledVersion(wingetId))
                    : (false, string.Empty);
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[InstalledAppsService] Проверка установки пакета {wingetId} (winget list --id): {ex.Message}");
                return (false, string.Empty);
            }
        }

        // internal, а не private: единственное, что можно проверить юнит-тестом без
        // живого winget — что запрашивается ровно один пакет, а не весь список.
        internal static string[] SinglePackageArgs(string wingetId) =>
            WingetArgs.Query("list", "--id", wingetId, "--exact");

        /// <summary>
        /// Разбирает вывод `winget list` (уже очищенный от ANSI) в словарь
        /// «токен → версия». internal — вызывается тестами напрямую, чтобы проверить
        /// разбор без запуска winget.
        /// </summary>
        internal void LoadFromOutput(string output)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(output))
            {
                var lines = output.Replace("\r\n", "\n").Split('\n');

                // Строка-разделитель ("-----") идёт сразу после заголовка с именами колонок.
                int sepIndex = Array.FindIndex(lines, WingetRunner.IsTableSeparator);
                int versionColumn = sepIndex > 0 ? FindVersionColumn(lines[sepIndex - 1]) : -1;

                // Проход 1 — только строки таблицы, сверху вниз. Первое вхождение
                // токена выигрывает: прежний GetInstalledVersion возвращал версию из
                // ПЕРВОЙ строки после разделителя, содержащей искомый ID.
                if (versionColumn >= 0)
                {
                    for (int i = sepIndex + 1; i < lines.Length; i++)
                        AddTokens(map, lines[i], ExtractVersion(lines[i], versionColumn));
                }

                // Проход 2 — остальные токены вывода (заголовок, футер, служебные
                // строки winget) с пустой версией. Нужен для точного сохранения
                // прежнего поведения IsInstalled: тот искал токен по ВСЕМУ выводу, а
                // не только по строкам таблицы, и при выводе без распознанной таблицы
                // (нет разделителя/колонки Version) всё равно мог ответить «установлено».
                // Порядок проходов важен: строки таблицы уже заняли свои токены
                // настоящими версиями, TryAdd их не перезапишет пустой строкой.
                foreach (var line in lines)
                    AddTokens(map, line, string.Empty);
            }

            _installed = map;
        }

        // Токены строки — максимальные последовательности непробельных символов.
        // Прежний поиск подстроки с проверкой «слева и справа пробел» находил ровно
        // такой токен целиком, поэтому набор совпадений не меняется.
        private static void AddTokens(Dictionary<string, string> map, string line, string version)
        {
            foreach (var token in line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                map.TryAdd(token, version);
        }

        // Версия строки таблицы: первое слово, начиная с позиции колонки Version.
        // Колонки `winget list` выровнены пробелами, поэтому «следующее слово» после Id
        // могло бы оказаться соседней колонкой (Available и т.п.) — берём строго по
        // позиции колонки из строки заголовка. Строка короче позиции колонки — версии нет.
        private static string ExtractVersion(string line, int versionColumn)
        {
            if (line.Length <= versionColumn) return string.Empty;

            string rest = line.Substring(versionColumn).TrimStart();
            int space = rest.IndexOfAny(VersionValueSeparators);
            return space < 0 ? rest : rest.Substring(0, space);
        }

        public bool IsInstalled(string wingetId)
        {
            if (string.IsNullOrEmpty(wingetId)) return false;
            return _installed.ContainsKey(wingetId);
        }

        public string GetInstalledVersion(string wingetId)
        {
            if (string.IsNullOrEmpty(wingetId)) return string.Empty;
            return _installed.TryGetValue(wingetId, out var version) ? version : string.Empty;
        }

        // Позиция начала колонки Version в строке заголовка (по символу 'V' слова "Version").
        // Поддерживаем нелокализованный и русский заголовок на случай локализации winget.
        private static int FindVersionColumn(string header)
        {
            foreach (var key in VersionHeaderKeys)
            {
                int idx = header.IndexOf(key, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0) return idx;
            }
            return -1;
        }
    }
}
