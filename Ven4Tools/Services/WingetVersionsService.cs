using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Ven4Tools.Services
{
    public static class WingetVersionsService
    {
        public static async Task<List<string>> FetchVersionsAsync(string wingetId, CancellationToken token = default)
        {
            if (!CommandLineGuard.ValidateId(wingetId)) return new List<string>();
            try
            {
                // WingetArgs.Query добавляет --accept-source-agreements/--disable-interactivity:
                // без них winget на машине, где соглашение источника ещё не принято
                // (свежая Windows — типовой сценарий для установщика ПО), вместо списка
                // версий печатает запрос подтверждения и завершается ошибкой — список
                // версий молча оказывался пустым, и выбор версии в каталоге не работал.
                var (_, output) = await WingetRunner.RunAsync(
                    WingetArgs.Query("show", "--id", wingetId, "--versions", "-e", "--source", "winget"),
                    token: token);

                return ParseVersions(output);
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[WingetVersionsService] Получение списка версий «{wingetId}»: {ex.Message}");
                return new List<string>();
            }
        }

        private static List<string> ParseVersions(string output)
        {
            // ANSI-последовательности убираем перед разбором — как и остальные
            // разборы вывода winget. Без этого escape-код в начале строки-разделителя
            // не давал WingetRunner.IsTableSeparator её распознать, pastSeparator
            // никогда не выставлялся, и список версий оказывался пустым.
            var lines = WingetRunner.StripAnsi(output)
                .Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            bool pastSeparator = false;
            var versions = new List<string>();

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (!pastSeparator)
                {
                    if (WingetRunner.IsTableSeparator(line)) pastSeparator = true;
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(trimmed))
                    versions.Add(trimmed);
            }

            return versions;
        }
    }
}
