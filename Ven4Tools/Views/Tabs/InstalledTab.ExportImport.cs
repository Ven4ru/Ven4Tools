using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Ven4Tools.Models;
using Ven4Tools.Services;

namespace Ven4Tools.Views.Tabs
{
    public partial class InstalledTab : UserControl
    {
        // ── Экспорт / Импорт ─────────────────────────────────────────────────

        private async void BtnExport_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title    = "Экспорт списка приложений",
                Filter   = "Winget package list (*.winget)|*.winget|JSON (*.json)|*.json",
                FileName = $"Ven4Tools-export-{DateTime.Now:yyyy-MM-dd}"
            };
            if (dlg.ShowDialog() != true) return;

            btnExport.IsEnabled = false;
            AppLogger.Write($"📤 Экспорт в {System.IO.Path.GetFileName(dlg.FileName)}...");
            try
            {
                var (_, output) = await WingetRunner.RunAsync($"export -o \"{dlg.FileName}\" --accept-source-agreements");
                bool ok = System.IO.File.Exists(dlg.FileName);
                AppLogger.Write(ok ? $"✅ Экспортировано → {dlg.FileName}"
                       : $"⚠ winget: {output.Trim().Split('\n').LastOrDefault()}");
            }
            catch (Exception ex) { AppLogger.Write($"❌ Ошибка экспорта: {ex.Message}"); }
            finally { btnExport.IsEnabled = true; }
        }

        private async void BtnImport_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title  = "Импорт списка приложений",
                Filter = "Winget package list (*.winget)|*.winget|JSON (*.json)|*.json"
            };
            if (dlg.ShowDialog() != true) return;

            var res = MessageBox.Show(
                $"Будет запущена массовая установка всех пакетов из файла:\n\n{dlg.FileName}\n\nПродолжить?",
                "Подтверждение импорта", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (res != MessageBoxResult.Yes) return;

            // Общий семафор с каталогом/историей/Windows Update — массовый winget import
            // не должен идти параллельно с другой установкой (конфликт msiexec, ошибка 1618,
            // частично применённый импорт). Ранний выход по IsBusy — до любых UI-мутаций.
            if (UiGuards.WarnIfInstallBusy()) return;

            var rpOutcome = await UiGuards.ConfirmAndCreateRestorePointAsync(
                "Импорт может установить сразу много приложений.\n\nСоздать точку восстановления Windows перед импортом?",
                "Ven4Tools — перед импортом списка");
            if (rpOutcome == RestorePointOutcome.Cancelled) return;

            btnImport.IsEnabled = false;
            AppLogger.Write($"📥 Импорт из {System.IO.Path.GetFileName(dlg.FileName)}...");
            AppLogger.Write("⏳ Это может занять несколько минут...");
            await InstallationService.InstallSemaphore.WaitAsync();
            try
            {
                var (_, output) = await WingetRunner.RunAsync($"import -i \"{dlg.FileName}\" --accept-package-agreements --accept-source-agreements");
                bool ok = output.Contains("успешно") || output.Contains("successfully") || output.Contains("All packages");
                AppLogger.Write(ok ? "✅ Импорт завершён"
                       : $"⚠ {output.Trim().Split('\n').LastOrDefault(l => !string.IsNullOrWhiteSpace(l))}");
                if (ok) await LoadAppsAsync();
            }
            catch (Exception ex) { AppLogger.Write($"❌ Ошибка импорта: {ex.Message}"); }
            finally
            {
                InstallationService.InstallSemaphore.Release();
                btnImport.IsEnabled = true;
            }
        }
    }
}
