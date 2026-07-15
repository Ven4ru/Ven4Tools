using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Ven4Tools.Models;
using Ven4Tools.Services;
using Ven4Tools.Shared;

namespace Ven4Tools.Views.Tabs
{
    public partial class SystemTab : UserControl
    {

        private async Task LoadSystemInfoAsync()
        {
            try
            {
                // Значения, читаемые быстро на UI-потоке
                string osVersion  = Environment.OSVersion.VersionString;
                var version       = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                string appVersion = version?.ToString() ?? "—";

                // WMI-запросы могут занимать 100-500 мс — выполняем в фоне, чтобы не морозить UI
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
                            long totalMemory = Convert.ToInt64(obj["TotalVisibleMemorySize"]) / 1024 / 1024;
                            ram = $"{totalMemory} ГБ";
                            break;
                        }
                    }
                });

                // Обновление UI — уже на UI-потоке после await, Dispatcher не нужен
                txtOSVersion.Text  = osVersion;
                txtProcessor.Text  = processor;
                txtRAM.Text        = ram;
                txtAppVersion.Text = appVersion;
            }
            catch (Exception ex)
            {
                AppLogger.Write($"❌ Ошибка загрузки информации о системе: {ex.Message}");
            }
        }

        private void BtnCopySystemInfo_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string info = $"ОС: {txtOSVersion.Text}\n" +
                              $"Процессор: {txtProcessor.Text}\n" +
                              $"ОЗУ: {txtRAM.Text}\n" +
                              $"Ven4Tools: {txtAppVersion.Text}";

                Clipboard.SetText(info);
                AppLogger.Write("📋 Информация о системе скопирована в буфер обмена");
            }
            catch (Exception ex)
            {
                AppLogger.Write($"❌ Ошибка копирования: {ex.Message}");
            }
        }

        private void BtnOpenLogs_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string logsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ven4Tools", "logs");
                Directory.CreateDirectory(logsPath);
                Process.Start(Ven4Tools.Services.TrustedExecutablePaths.ExplorerExe, logsPath);
                AppLogger.Write($"📁 Открыта папка логов: {logsPath}");
            }
            catch (Exception ex)
            {
                AppLogger.Write($"❌ Ошибка открытия папки логов: {ex.Message}");
            }
        }

        private void BtnOpenLatestLog_Click(object sender, RoutedEventArgs e)
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
                txtLatestLog.Text = preview;

                Process.Start(new ProcessStartInfo { FileName = Ven4Tools.Services.TrustedExecutablePaths.NotepadExe, Arguments = latestLog, UseShellExecute = true });
                AppLogger.Write($"📄 Открыт лог: {Path.GetFileName(latestLog)}");
            }
            catch (Exception ex)
            {
                AppLogger.Write($"❌ Ошибка: {ex.Message}");
            }
        }

        private async void BtnCheckUpdates_Click(object sender, RoutedEventArgs e)
        {
            btnCheckUpdates.IsEnabled = false;
            txtUpdatesLog.Text = "⏳ Проверка...";
            try
            {
                var (_, raw) = await WingetRunner.RunAsync(
                    "upgrade --include-unknown --source winget",
                    TimeSpan.FromMinutes(3));

                var upgradable = raw.Split('\n')
                    .Select(l => WingetRunner.StripAnsi(l).Trim())
                    .Where(l => !string.IsNullOrWhiteSpace(l)
                             && !l.StartsWith("Name")
                             && !l.StartsWith("-")
                             && !l.StartsWith("The ")
                             && l.Contains("  "))
                    .ToList();

                if (upgradable.Count > 0)
                {
                    txtUpdatesLog.Text = $"🔔 Доступно обновлений: {upgradable.Count}\n\n" + string.Join("\n", upgradable);
                    AppLogger.Write($"🔔 Доступно обновлений winget: {upgradable.Count}");
                }
                else
                {
                    txtUpdatesLog.Text = "✅ Все установленные приложения актуальны";
                    AppLogger.Write("✅ Обновлений winget не найдено");
                }
            }
            catch (Exception ex)
            {
                txtUpdatesLog.Text = $"❌ Ошибка: {ex.Message}";
                AppLogger.Write($"❌ Ошибка проверки обновлений: {ex.Message}");
            }
            finally
            {
                btnCheckUpdates.IsEnabled = true;
            }
        }

        private void BtnClearLogs_Click(object sender, RoutedEventArgs e)
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
                        AppLogger.Write("🗑️ Логи очищены");
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Write($"❌ Ошибка очистки логов: {ex.Message}");
                }
            }
        }
    }
}
