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
        private async void BtnDeleteClient_Click(object sender, RoutedEventArgs e)
        {
            if (_isUiTestMode)
            {
                AddLog("UI test: удаление клиента");
                return;
            }

            var answer = System.Windows.MessageBox.Show(
                "Будет удалено:\n" +
                $"• Папка клиента: {_clientPath}\n" +
                "• Ярлыки на рабочем столе\n" +
                "• Ярлыки в меню Пуск\n" +
                "• Запись автозапуска в реестре\n\n" +
                "Настройки и логи лаунчера сохраняются.\n\n" +
                "Продолжить?",
                "Удаление клиента Ven4Tools",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (answer != MessageBoxResult.Yes) return;

            if (!InstallPathGuard.IsClientPathSafe(_clientPath, _dataFolderPath))
            {
                AddLog($"⛔ Удаление отменено: папка клиента указывает на защищённую папку ({_clientPath})");
                System.Windows.MessageBox.Show(
                    $"Папка клиента:\n{_clientPath}\n\n" +
                    "совпадает с защищённой пользовательской папкой (Downloads/Документы/Рабочий стол " +
                    "и т.п.) целиком. Удаление отменено во избежание потери данных.",
                    "Небезопасный путь установки", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            btnDeleteClient.IsEnabled = false;
            AddLog("🗑️ Удаление клиента...");

            await Task.Run(() =>
            {
                if (Directory.Exists(_clientPath))
                {
                    try { Directory.Delete(_clientPath, true); AddLog("   ✅ Папка клиента удалена"); }
                    catch (Exception ex) { AddLog($"   ⚠️ Папка клиента: {ex.Message}"); }
                }
                else
                {
                    AddLog("   ℹ️ Папка клиента не найдена");
                }

                string[] desktops = {
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
                };
                string[] startMenuRoots = {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs")
                };
                ClientShortcutCleaner.Clean(desktops, startMenuRoots);
                AddLog("   ✅ Ярлыки клиента проверены");

                try
                {
                    using var runKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", writable: true);
                    runKey?.DeleteValue("Ven4Tools", throwOnMissingValue: false);
                    runKey?.DeleteValue("Ven4Tools Client", throwOnMissingValue: false);
                    AddLog("   ✅ Записи автозапуска клиента удалены");
                }
                catch (Exception ex) { AddLog($"   ⚠️ Реестр: {ex.Message}"); }

                // Корневую папку %LocalAppData%\Ven4Tools не трогаем: в ней лежат
                // настройки и логи работающего лаунчера. После удаления пересоздаём
                // папку клиента, чтобы состояние осталось консистентным.
                try { Directory.CreateDirectory(_clientPath); } catch { }
            });

            Dispatcher.Invoke(() =>
            {
                SetLaunchButtonState(LaunchButtonState.Download);
                btnDeleteClient.IsEnabled = true;
            });

            AddLog("✅ Удаление завершено");
        }
    }
}
