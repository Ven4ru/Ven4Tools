using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Threading.Tasks;
using System.Windows;
using Ven4Tools.Services;

namespace Ven4Tools.ViewModels
{
    public sealed partial class DiagnosticsViewModel
    {
        private async Task LoadSystemInfoAsync()
        {
            try
            {
                string osVersion  = Environment.OSVersion.VersionString;
                var version       = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                string appVersion = version?.ToString() ?? "—";

                string processor = "Неизвестно";
                string ram       = "";

                await Task.Run(() =>
                {
                    using (var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor"))
                    {
                        foreach (var obj in searcher.Get())
                        {
                            processor = obj["Name"]?.ToString()?.Trim() ?? "Неизвестно";
                            break;
                        }
                    }

                    using (var searcher = new ManagementObjectSearcher("SELECT TotalVisibleMemorySize FROM Win32_OperatingSystem"))
                    {
                        foreach (var obj in searcher.Get())
                        {
                            // TotalVisibleMemorySize от WMI приходит в КБ, не в байтах —
                            // домножаем перед общим байтовым форматтером.
                            long totalMemoryKB = Convert.ToInt64(obj["TotalVisibleMemorySize"]);
                            ram = Helpers.SizeFormatter.BytesToGBWhole(totalMemoryKB * 1024L);
                            break;
                        }
                    }
                });

                OSVersionText  = osVersion;
                ProcessorText  = processor;
                RAMText        = ram;
                AppVersionText = appVersion;
            }
            catch (Exception ex)
            {
                AppLogger.Write($"❌ Ошибка загрузки информации о системе: {ex.Message}");
            }
        }

        private void CopySystemInfo()
        {
            try
            {
                string info = $"ОС: {OSVersionText}\n" +
                              $"Процессор: {ProcessorText}\n" +
                              $"ОЗУ: {RAMText}\n" +
                              $"Ven4Tools: {AppVersionText}";

                Clipboard.SetText(info);
                AppLogger.Write("📋 Информация о системе скопирована в буфер обмена");
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "DiagnosticsViewModel.CopySystemInfo");
                MessageBox.Show("Не удалось скопировать информацию о системе: " + ex.Message, "Ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OpenLogs()
        {
            try
            {
                string logsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ven4Tools", "logs");
                Directory.CreateDirectory(logsPath);
                // Путь в кавычках: он лежит в профиле пользователя, а имя учётной
                // записи Windows вполне может содержать пробел («Иван Петров») —
                // без кавычек explorer получил бы обрезанный по пробелу путь.
                Process.Start(TrustedExecutablePaths.ExplorerExe, $"\"{logsPath}\"");
                AppLogger.Write($"📁 Открыта папка логов: {logsPath}");
            }
            catch (Exception ex)
            {
                AppLogger.Write($"❌ Ошибка открытия папки логов: {ex.Message}");
            }
        }

        private void OpenLatestLog()
        {
            try
            {
                string logsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ven4Tools", "logs");
                if (!Directory.Exists(logsPath)) { AppLogger.Write("📋 Логов нет"); return; }

                var latestLog = Directory.GetFiles(logsPath, "install_*.log")
                    .OrderByDescending(f => f)
                    .FirstOrDefault();

                if (latestLog == null) { AppLogger.Write("📋 Файлы логов не найдены"); return; }

                var lines = File.ReadAllLines(latestLog);
                var preview = string.Join("\n", lines.Skip(Math.Max(0, lines.Length - 50)));
                LatestLogText = preview;

                // Кавычки обязательны по той же причине, что и у кнопки «Открыть папку
                // логов»: путь идёт через профиль пользователя, имя которого может
                // содержать пробел, и «блокнот» открыл бы не тот файл.
                Process.Start(new ProcessStartInfo { FileName = TrustedExecutablePaths.NotepadExe, Arguments = $"\"{latestLog}\"", UseShellExecute = true });
                AppLogger.Write($"📄 Открыт лог: {Path.GetFileName(latestLog)}");
            }
            catch (Exception ex)
            {
                AppLogger.Write($"❌ Ошибка: {ex.Message}");
            }
        }

        private void ClearLogs()
        {
            var result = MessageBox.Show("Удалить все файлы логов?", "Подтверждение",
                MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    string logsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ven4Tools", "logs");
                    if (Directory.Exists(logsPath))
                    {
                        foreach (var file in Directory.GetFiles(logsPath))
                        {
                            File.Delete(file);
                        }
                    }
                    // Общий журнал приложения (app.log / app.old.log) лежит НЕ в подпапке
                    // logs, а уровнем выше — раньше он переживал очистку, хотя вопрос
                    // пользователю звучит «Удалить все файлы логов?». Именно там копятся
                    // сообщения вкладок и сервисов, поэтому обещание должно выполняться.
                    AppLogger.ClearAppLogFiles();
                    AppLogger.Write("🗑️ Логи очищены");
                }
                catch (Exception ex)
                {
                    AppLogger.Write($"❌ Ошибка очистки логов: {ex.Message}");
                }
            }
        }
    }
}
