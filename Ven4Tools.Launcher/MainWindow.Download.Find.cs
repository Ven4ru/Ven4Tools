using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Ven4Tools.Launcher.Models;
using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Launcher
{
    public partial class MainWindow
    {
        private async void BtnFindClient_Click(object sender, RoutedEventArgs e)
        {
            if (_isUiTestMode)
            {
                AddLog("UI test: поиск клиента");
                return;
            }

            btnFindClient.IsEnabled = false;
            AddLog("🔍 Поиск Ven4Tools.exe на диске...");

            try
            {
                var found = await Task.Run(() =>
                {
                    var results = new List<string>();
                    foreach (var root in GetClientSearchRoots())
                    {
                        if (!Directory.Exists(root)) continue;
                        results.AddRange(EnumerateFilesSafe(root, LauncherPaths.ClientExeName));
                    }
                    return results;
                });

                if (found.Count == 0)
                {
                    AddLog("❌ Ven4Tools.exe не найден в стандартных папках");
                    System.Windows.MessageBox.Show(
                        "Ven4Tools.exe не найден в:\n" +
                        "• Program Files / Program Files (x86)\n" +
                        "• Документы / Documents\n" +
                        "• Загрузки / Downloads\n" +
                        "• Рабочий стол\n\n" +
                        "Укажите папку вручную — кнопка «Изменить…» в карточке «Папка установки».",
                        "Не найдено", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                foreach (var f in found)
                    AddLog($"   📄 {f}");

                // Раньше при нескольких находках лаунчер брал found[0] — то есть какую
                // придётся, порядок задавался обходом каталогов — и применял её БЕЗ
                // вопроса, показывая лишь уведомление с кнопкой «ОК». Подтверждение
                // спрашивалось только когда копия одна, то есть ровно в том случае, где
                // выбирать не из чего. На живом прогоне 10.09.2026 это молча увело папку
                // установки с рабочего клиента 5.1.1 на его же старый бэкап 5.0.0.
                // Теперь кандидаты ранжируются (текущая папка → свежая версия →
                // более короткий путь), а применение всегда требует «Да».
                var ordered = RankClientCandidates(found, _clientPath);
                string chosen = ordered[0];

                string question = ordered.Count > 1
                    ? $"Найдено копий: {ordered.Count}. Больше всего похожа на рабочую:\n\n{chosen}\n\n" +
                      "Использовать её?\n(«Нет» — выбрать папку вручную)\n\nОстальные найденные:\n" +
                      string.Join("\n", ordered.Skip(1).Select((f, i) => $"{i + 2}. {f}"))
                    : $"Найдено:\n{chosen}\n\nИспользовать эту папку?";

                if (System.Windows.MessageBox.Show(
                        question, ordered.Count > 1 ? "Найдено несколько" : "Ven4Tools найден",
                        MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                {
                    // Отказ не должен быть тупиком: сразу открываем тот же выбор папки,
                    // что и кнопка «Изменить…» — иначе пользователю остаётся только
                    // догадаться, куда идти дальше.
                    AddLog("ℹ️ Автоматически найденная папка отклонена — открываю выбор папки вручную");
                    BtnSelectFolder_Click(sender, e);
                    return;
                }

                string candidatePath = Path.GetDirectoryName(chosen)!;
                if (!InstallPathGuard.IsClientPathSafe(candidatePath, _dataFolderPath))
                {
                    AddLog($"⛔ Найденный Ven4Tools.exe лежит прямо в защищённой папке ({candidatePath}) — путь не принят");
                    System.Windows.MessageBox.Show(
                        $"Ven4Tools.exe найден прямо в:\n{candidatePath}\n\n" +
                        "Эта папка не может стать папкой установки клиента целиком — при обновлении " +
                        "или удалении её содержимое было бы уничтожено.\n\n" +
                        "Переместите клиент в отдельную подпапку или укажите её вручную — " +
                        "кнопка «Изменить…» в карточке «Папка установки».",
                        "Небезопасный путь установки", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                _clientPath  = candidatePath;
                _installPath = Path.GetDirectoryName(_clientPath) ?? _clientPath;
                txtInstallPath.Text = _clientPath;
                SaveSettings();
                AddLog($"✅ Папка установки: {_clientPath}");
                CheckExistingClient();
            }
            catch (Exception ex)
            {
                AddLog($"❌ Ошибка поиска: {ex.Message}");
            }
            finally
            {
                btnFindClient.IsEnabled = true;
            }
        }

        /// <summary>
        /// Ранжирует найденные копии Ven4Tools.exe так, чтобы первой шла та, которую
        /// пользователь скорее всего и считает рабочей. Порядок обхода каталогов для
        /// этого не годится: в нём бэкап «Ven4Tools_Client_backup_*» легко опережает
        /// настоящую установку.
        ///
        /// Приоритеты, по убыванию:
        ///   1. текущая папка установки — если клиент уже привязан, менять привязку не за чем;
        ///   2. более свежая версия файла — бэкап предыдущего релиза уступает актуальному;
        ///   3. путь не похож на резервную копию (backup/бэкап/copy/old в любом сегменте);
        ///   4. более короткий путь — установка обычно лежит выше по дереву, чем сборки.
        /// Метод чистый (кроме чтения версии файла) и не меняет состояние.
        /// </summary>
        internal static List<string> RankClientCandidates(IEnumerable<string> found, string? currentClientPath)
        {
            string? current = string.IsNullOrWhiteSpace(currentClientPath)
                ? null
                : SafeFullPath(currentClientPath);

            return found
                .Select(path => new
                {
                    Path      = path,
                    IsCurrent = current != null &&
                                string.Equals(SafeFullPath(Path.GetDirectoryName(path) ?? path), current,
                                              StringComparison.OrdinalIgnoreCase),
                    Version   = ReadFileVersion(path),
                    LooksLikeBackup = LooksLikeBackupPath(path),
                    Depth     = path.Length
                })
                .OrderByDescending(c => c.IsCurrent)
                .ThenByDescending(c => c.Version)
                .ThenBy(c => c.LooksLikeBackup)
                .ThenBy(c => c.Depth)
                .Select(c => c.Path)
                .ToList();
        }

        private static readonly string[] BackupMarkers = { "backup", "бэкап", "бекап", "_old", ".old", "copy", "копия" };

        private static bool LooksLikeBackupPath(string path) =>
            BackupMarkers.Any(marker => path.Contains(marker, StringComparison.OrdinalIgnoreCase));

        private static Version ReadFileVersion(string path)
        {
            try
            {
                var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
                return new Version(
                    Math.Max(info.FileMajorPart, 0), Math.Max(info.FileMinorPart, 0),
                    Math.Max(info.FileBuildPart, 0), Math.Max(info.FilePrivatePart, 0));
            }
            // Файл недоступен или не несёт версии — такая копия просто уходит вниз списка.
            catch { return new Version(0, 0, 0, 0); }
        }

        private static string SafeFullPath(string path)
        {
            try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar); }
            catch { return path.TrimEnd(Path.DirectorySeparatorChar); }
        }

        // Рекурсивный поиск файла по маске, устойчивый к недоступным подпапкам.
        // Directory.EnumerateFiles(..., AllDirectories) — ленивый: реальный обход идёт
        // при итерации, а не при вызове, поэтому недоступная подпапка где-то в глубине
        // (например C:\Program Files\WindowsApps) молча обрывала бы обход всего корня, и
        // реально существующий Ven4Tools.exe глубже проблемной точки не находился бы.
        // Directory.GetFiles/GetDirectories — НЕ ленивые: бросают сразу, поэтому try/catch
        // вокруг них ловит недоступность каждой папки отдельно, а остальное дерево
        // продолжает сканироваться (тот же паттерн, что AppLaunchResolver.EnumerateLnkFilesSafe
        // в клиенте). Пропуск недоступной папки — штатная ситуация, не логируется.
        private static IEnumerable<string> EnumerateFilesSafe(string root, string searchPattern)
        {
            var result = new List<string>();
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                string dir = stack.Pop();

                string[] files;
                try { files = Directory.GetFiles(dir, searchPattern); }
                catch { continue; } // недоступна сама папка — пропускаем её файлы, не всё дерево
                result.AddRange(files);

                string[] subDirs;
                try { subDirs = Directory.GetDirectories(dir); }
                catch { continue; } // недоступен список подпапок — глубже не идём, но остальное дерево не страдает
                foreach (var sub in subDirs) stack.Push(sub);
            }
            return result;
        }

        private static IEnumerable<string> GetClientSearchRoots()
        {
            yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            yield return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            yield return Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

            foreach (var downloads in DownloadsFolderResolver.GetExistingCandidates())
                yield return downloads;
        }
    }
}
