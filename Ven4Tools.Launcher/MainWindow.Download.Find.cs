using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
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
                        "Воспользуйтесь кнопкой «Выбрать папку» для ручного указания пути.",
                        "Не найдено", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                foreach (var f in found)
                    AddLog($"   📄 {f}");

                string chosen = found[0];
                if (found.Count > 1)
                {
                    var list = string.Join("\n", found.Select((f, i) => $"{i + 1}. {f}"));
                    System.Windows.MessageBox.Show(
                        $"Найдено {found.Count} экземпляра(ов).\nБудет использован первый:\n\n{chosen}\n\nПолный список:\n{list}",
                        "Найдено несколько", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    var result = System.Windows.MessageBox.Show(
                        $"Найдено:\n{chosen}\n\nИспользовать эту папку?",
                        "Ven4Tools найден", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (result != MessageBoxResult.Yes) return;
                }

                string candidatePath = Path.GetDirectoryName(chosen)!;
                if (!InstallPathGuard.IsClientPathSafe(candidatePath, _dataFolderPath))
                {
                    AddLog($"⛔ Найденный Ven4Tools.exe лежит прямо в защищённой папке ({candidatePath}) — путь не принят");
                    System.Windows.MessageBox.Show(
                        $"Ven4Tools.exe найден прямо в:\n{candidatePath}\n\n" +
                        "Эта папка не может стать папкой установки клиента целиком — при обновлении " +
                        "или удалении её содержимое было бы уничтожено.\n\n" +
                        "Переместите клиент в отдельную подпапку или воспользуйтесь кнопкой «Выбрать папку».",
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

            string? downloads = null;
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders");
                downloads = key?.GetValue("{374DE290-123F-4565-9164-39C4925E467B}")?.ToString();
            }
            catch { }

            if (!string.IsNullOrEmpty(downloads) && Directory.Exists(downloads))
            {
                yield return downloads;
            }
            else
            {
                string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                foreach (var name in new[] { "Downloads", "Загрузки" })
                {
                    var path = Path.Combine(userProfile, name);
                    if (Directory.Exists(path)) yield return path;
                }
            }
        }
    }
}
