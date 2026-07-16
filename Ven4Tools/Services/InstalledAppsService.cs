using System;
using System.Threading.Tasks;

namespace Ven4Tools.Services
{
    public class InstalledAppsService
    {
        private string _rawOutput = string.Empty;

        public async Task RefreshAsync()
        {
            try
            {
                var (_, output) = await WingetRunner.RunAsync(new[]
                {
                    "list", "--accept-source-agreements", "--disable-interactivity"
                });
                _rawOutput = output;
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[InstalledAppsService] Получение списка установленных приложений (winget list): {ex.Message}");
                _rawOutput = string.Empty;
            }
        }

        public bool IsInstalled(string wingetId)
        {
            if (string.IsNullOrEmpty(wingetId) || string.IsNullOrEmpty(_rawOutput))
                return false;

            return ContainsWhitespaceBoundedToken(_rawOutput, wingetId);
        }

        // Раньше здесь на каждый вызов собирался Regex вида (?<!\S){id}(?!\S): так как
        // паттерн зависит от wingetId (динамический), кэш Regex не помогал и каждая
        // проверка перекомпилировала выражение. IsInstalled/GetInstalledVersion
        // вызываются в цикле по всем приложениям каталога (сотни), поэтому заменено
        // на прямой поиск подстроки, ограниченной пробельными символами с обеих сторон
        // — семантически эквивалентно исходному regex, но без компиляции на каждый вызов.
        private static bool ContainsWhitespaceBoundedToken(string haystack, string token)
        {
            int idx = 0;
            while ((idx = haystack.IndexOf(token, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                bool leftBoundary = idx == 0 || char.IsWhiteSpace(haystack[idx - 1]);
                int end = idx + token.Length;
                bool rightBoundary = end >= haystack.Length || char.IsWhiteSpace(haystack[end]);
                if (leftBoundary && rightBoundary) return true;
                idx++;
            }
            return false;
        }

        public string GetInstalledVersion(string wingetId)
        {
            if (string.IsNullOrEmpty(wingetId) || string.IsNullOrEmpty(_rawOutput))
                return string.Empty;

            // Колонки `winget list` выровнены пробелами, поэтому «следующее слово»
            // после Id может оказаться соседней колонкой (Available и т.п.).
            // Берём версию строго по позиции колонки Version из строки заголовка.
            var lines = _rawOutput.Replace("\r\n", "\n").Split('\n');

            // Строка-разделитель ("-----") идёт сразу после заголовка с именами колонок.
            int sepIndex = Array.FindIndex(lines, WingetRunner.IsTableSeparator);
            if (sepIndex <= 0) return string.Empty;

            int versionColumn = FindVersionColumn(lines[sepIndex - 1]);
            if (versionColumn < 0) return string.Empty;

            for (int i = sepIndex + 1; i < lines.Length; i++)
            {
                string line = lines[i];
                if (!ContainsWhitespaceBoundedToken(line, wingetId))
                    continue;

                // Строка приложения короче позиции колонки Version — версии нет.
                if (line.Length <= versionColumn)
                    return string.Empty;

                // Первое слово, начиная с позиции колонки Version.
                string rest = line.Substring(versionColumn).TrimStart();
                int space = rest.IndexOfAny(new[] { ' ', '\t' });
                return space < 0 ? rest : rest.Substring(0, space);
            }

            return string.Empty;
        }

        // Позиция начала колонки Version в строке заголовка (по символу 'V' слова "Version").
        // Поддерживаем нелокализованный и русский заголовок на случай локализации winget.
        private static int FindVersionColumn(string header)
        {
            foreach (var key in new[] { "Version", "Версия" })
            {
                int idx = header.IndexOf(key, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0) return idx;
            }
            return -1;
        }
    }
}
