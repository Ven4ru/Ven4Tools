using System;
using System.Collections.Generic;
using System.IO;

namespace Ven4Tools.Launcher.Services
{
    // Единый резолвер папки "Загрузки" — раньше InstallPathGuard.GetDownloadsFolder()
    // и MainWindow.Download.Find.cs.GetClientSearchRoots() дублировали эту логику
    // по-разному (комментарий в InstallPathGuard утверждал идентичность, хотя её не
    // было): защита не проверяла Directory.Exists и не имела фоллбэка, поэтому
    // протухшее значение реестра (папка "Загрузки" перенесена на отключённый диск)
    // не попадало в список защищённых корней, а поиск через фоллбэк всё равно
    // находил реальную "Загрузки" в профиле — защита от уничтожения содержимого
    // была fail-open именно в том сценарии, ради которого писалась (аудит 2026-07-13).
    internal static class DownloadsFolderResolver
    {
        // Все существующие на диске кандидаты на папку "Загрузки": значение из реестра
        // (если оно раскрывается в реально существующий путь) плюс оба локализованных
        // варианта в профиле пользователя — их может быть больше одного одновременно.
        public static IEnumerable<string> GetExistingCandidates()
        {
            string? fromRegistry = TryGetFromRegistry();
            if (fromRegistry != null)
            {
                yield return fromRegistry;
            }

            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            foreach (var name in new[] { "Downloads", "Загрузки" })
            {
                var path = Path.Combine(userProfile, name);
                if (!string.Equals(path, fromRegistry, StringComparison.OrdinalIgnoreCase) &&
                    Directory.Exists(path))
                {
                    yield return path;
                }
            }
        }

        private static string? TryGetFromRegistry()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders");
                var raw = key?.GetValue("{374DE290-123F-4565-9164-39C4925E467B}")?.ToString();
                if (string.IsNullOrEmpty(raw)) return null;

                // Значение может быть нераскрытой строкой вида "%USERPROFILE%\Downloads" —
                // ExpandEnvironmentVariables безопасен для уже раскрытых путей (no-op).
                string expanded = Environment.ExpandEnvironmentVariables(raw);
                return Directory.Exists(expanded) ? expanded : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
