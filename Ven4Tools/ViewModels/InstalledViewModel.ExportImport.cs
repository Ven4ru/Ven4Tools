using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Ven4Tools.Services;

namespace Ven4Tools.ViewModels
{
    public sealed partial class InstalledViewModel
    {
        // ── Экспорт / Импорт ─────────────────────────────────────────────────

        private async Task RunExportAsync()
        {
            if (IsExporting) return;

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title    = "Экспорт списка приложений",
                Filter   = "Winget package list (*.winget)|*.winget|JSON (*.json)|*.json",
                FileName = $"Ven4Tools-export-{DateTime.Now:yyyy-MM-dd}"
            };
            if (dlg.ShowDialog() != true) return;

            IsExporting = true;
            AppLogger.Write($"📤 Экспорт в {System.IO.Path.GetFileName(dlg.FileName)}...");
            try
            {
                var (code, output) = await WingetRunner.RunAsync($"export -o \"{dlg.FileName}\" {WingetArgs.NonInteractiveLine}");
                // Одного File.Exists мало: SaveFileDialog разрешает выбрать уже
                // существующий файл, и при неудаче winget на диске остаётся СТАРЫЙ файл —
                // проверка проходила, и пользователь получал «✅ Экспортировано»
                // на устаревшие данные. Требуем ещё и нулевой код выхода.
                bool ok = code == 0 && System.IO.File.Exists(dlg.FileName);
                AppLogger.Write(ok ? $"✅ Экспортировано → {dlg.FileName}"
                       : $"⚠ winget: {output.Trim().Split('\n').LastOrDefault()}");
            }
            catch (Exception ex) { AppLogger.Write($"❌ Ошибка экспорта: {ex.Message}"); }
            finally { IsExporting = false; }
        }

        private async Task RunImportAsync()
        {
            if (IsImporting) return;

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
            // не должен идти параллельно с другой установкой. Ранний выход по IsBusy —
            // до любых UI-мутаций.
            if (Views.UiGuards.WarnIfInstallBusy()) return;

            var rpOutcome = await Views.UiGuards.ConfirmAndCreateRestorePointAsync(
                "Импорт может установить сразу много приложений.\n\nСоздать точку восстановления Windows перед импортом?",
                "Ven4Tools — перед импортом списка");
            if (rpOutcome == Views.RestorePointOutcome.Cancelled) return;

            IsImporting = true;
            AppLogger.Write($"📥 Импорт из {System.IO.Path.GetFileName(dlg.FileName)}...");
            AppLogger.Write("⏳ Это может занять несколько минут...");
            await InstallationService.InstallSemaphore.WaitAsync();
            try
            {
                // Успех определяется кодом выхода, а не поиском подстрок
                // «успешно»/«successfully» в выводе — проект принципиально не передаёт
                // --locale en-US, поэтому winget печатает на языке системы.
                var (code, output) = await WingetRunner.RunAsync($"import -i \"{dlg.FileName}\" {WingetArgs.ModifyLine}");
                var exit = DescribeWingetExitCode(code);

                if (exit.Success)
                    AppLogger.Write(exit.Reboot
                        ? "✅ Импорт завершён (для части пакетов требуется перезагрузка)"
                        : "✅ Импорт завершён");
                // code == -1 — синтетический признак «winget вообще не отработал»
                else if (code == -1)
                    AppLogger.Write("⚠ Импорт не выполнен: winget не отработал (причина — в логе выше)");
                else
                {
                    AppLogger.Write($"⚠ Импорт завершён с ошибками: {exit.Reason}");
                    string? lastLine = output.Trim().Split('\n')
                        .LastOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim();
                    if (!string.IsNullOrEmpty(lastLine)) AppLogger.Write($"   winget: {lastLine}");
                }

                // Обновляем список, если winget реально отработал: при частичной неудаче
                // часть пакетов всё равно установлена, и список обязан это отразить.
                if (code != -1) await LoadAppsAsync();
            }
            catch (Exception ex) { AppLogger.Write($"❌ Ошибка импорта: {ex.Message}"); }
            finally
            {
                InstallationService.InstallSemaphore.Release();
                IsImporting = false;
            }
        }
    }
}
