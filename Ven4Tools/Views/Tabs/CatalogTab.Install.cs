using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ven4Tools.Models;
using Ven4Tools.Services;
using Ven4Tools.Shared;

namespace Ven4Tools.Views.Tabs
{
    public partial class CatalogTab
    {
        private List<string> GetSelectedApps()
        {
            return appCheckBoxes
                .Where(kv => kv.Value.IsChecked == true && kv.Value.IsEnabled)
                .Select(kv => kv.Key)
                .ToList();
        }

        private void AppCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            UpdateInstallButton();
            if (sender is FrameworkElement element) MotionService.Pulse(element, 1.025, 120);
        }

        private void UpdateInstallButton()
        {
            int count = GetSelectedApps().Count;
            btnInstall.Content = count > 0
                ? $"Установить ({count})"
                : "Установить выбранные";
            txtSelectionBar.Text = count > 0
                ? $"Выбрано приложений: {count}"
                : "Ничего не выбрано";
            if (count > 0) MotionService.SlideIn(txtSelectionBar, 4, 140);
            // Во время установки доступностью кнопок управляет процесс установки
            if (!_isInstalling)
            {
                btnInstall.IsEnabled    = count > 0;
                btnSavePreset.IsEnabled = count > 0;
            }
        }

        private async void InstallSelected_Click(object sender, RoutedEventArgs e)
        {
            var selectedApps = GetSelectedApps();
            if (selectedApps.Count == 0)
            {
                MessageBox.Show("Выберите хотя бы одну программу!", "Ven4Tools", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Проверяем занятость общего семафора установки ДО любых UI-мутаций
            // (диалог точки восстановления, блокировка кнопок) — иначе пользователь
            // тратит время на диалог, а затем всё равно упирается в занятый семафор.
            if (InstallationService.IsBusy)
            {
                MessageBox.Show(
                    "Дождитесь завершения текущей установки, затем повторите попытку.",
                    "Установка занята", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Блокируем кнопку «Установить» ДО долгой операции создания точки
            // восстановления, иначе второй клик запускает параллельную установку.
            lstAppProgress.Items.Clear();
            installProgressBar.Value = 0;
            isCancelled = false;
            _isInstalling = true;
            btnInstall.IsEnabled = false;
            btnCancelInstall.IsEnabled = true;

            // Restore point — ask only for bulk installs (≥ 2 apps)
            if (selectedApps.Count >= 2)
            {
                var rpAnswer = MessageBox.Show(
                    $"Будет установлено {selectedApps.Count} приложений.\n\nСоздать точку восстановления Windows перед установкой?",
                    "Точка восстановления",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Question);

                if (rpAnswer == MessageBoxResult.Cancel)
                {
                    _isInstalling = false;
                    UpdateInstallButton();
                    btnCancelInstall.IsEnabled = false;
                    return;
                }

                if (rpAnswer == MessageBoxResult.Yes)
                {
                    AddLog("🛡️ Создаю точку восстановления...");
                    bool rpOk = await CreateRestorePointAsync();
                    AddLog(rpOk ? "✅ Точка восстановления создана" : "⚠️ Точка восстановления не создана (можно продолжать)");
                }
            }

            cancellationTokenSource = new CancellationTokenSource();
            var token = cancellationTokenSource.Token;

            var allApps      = appManager.GetAllApps();
            var appsToInstall = allApps.Where(a => selectedApps.Contains(a.Id)).ToList();
            var progressDict  = new Dictionary<string, AppInstallProgress>();

            // Capture version selections on UI thread before spawning tasks
            var versionSelections = new Dictionary<string, string?>();
            foreach (var app in appsToInstall)
            {
                if (_versionCombos.TryGetValue(app.Id, out var vc) && vc.SelectedItem is string sv && sv != "Последняя")
                    versionSelections[app.Id] = sv;
                else
                    versionSelections[app.Id] = null;
            }

            AddLog($"💾 Установка на диск: {selectedInstallDrive}");

            var progress = new Progress<AppInstallProgress>(p =>
            {
                progressDict[p.AppId] = p;
                var existing = lstAppProgress.Items.OfType<AppInstallProgress>().FirstOrDefault(x => x.AppId == p.AppId);
                if (existing != null)
                {
                    existing.Status     = p.Status;
                    existing.Percentage = p.Percentage;
                    lstAppProgress.Items.Refresh();
                }
                else
                {
                    lstAppProgress.Items.Add(p);
                }

                if (progressDict.Values.All(x => x.Percentage >= 100 || x.Status.Contains("Ошибка") || x.Status.Contains("Отменено")))
                    installProgressBar.Value = 100;
                else
                    installProgressBar.Value = progressDict.Values.Average(x => x.Percentage);
            });

            txtOverallStatus.Text = $"⏳ Установка 0/{appsToInstall.Count}...";
            int completed = 0, failed = 0;

            var pmConsentCache = new Dictionary<string, bool>();
            using var pmConsentLock = new System.Threading.SemaphoreSlim(1, 1);
            async Task<bool> ConfirmPmInstall(string pmName)
            {
                await pmConsentLock.WaitAsync();
                try
                {
                    if (pmConsentCache.TryGetValue(pmName, out bool cached)) return cached;
                    bool consented = await Dispatcher.InvokeAsync(() =>
                        MessageBox.Show(
                            $"Для установки приложения требуется {pmName}, который сейчас не установлен.\n\n" +
                            $"Разрешить автоматическую установку {pmName}?",
                            $"Установка {pmName}",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Question) == MessageBoxResult.Yes);
                    pmConsentCache[pmName] = consented;
                    return consented;
                }
                finally { pmConsentLock.Release(); }
            }

            var tasks = appsToInstall.Select(app => Task.Run(async () =>
            {
                await InstallationService.InstallSemaphore.WaitAsync();
                try
                {
                    if (token.IsCancellationRequested) return;
                    versionSelections.TryGetValue(app.Id, out var selectedVersion);
                    var result = await installService.InstallAppAsync(app, wingetSources, token, progress, selectedInstallDrive, selectedVersion, ConfirmPmInstall);
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (result.Success)
                        {
                            completed++;
                            if (selectedVersion != null && _appVersions.TryGetValue(app.Id, out var knownVer) && knownVer.Count > 0)
                                _versionTracker.TrackInstall(app.Id, selectedVersion, knownVer[0]);
                            if (appCheckBoxes.TryGetValue(app.Id, out var cb) && cb.Content is TextBlock tb)
                            {
                                tb.Foreground  = new SolidColorBrush(Color.FromRgb(136, 136, 136));
                                cb.Opacity     = 0.7;
                                cb.IsEnabled   = false;
                                cb.ToolTip     = selectedVersion != null ? $"✅ Установлено ({selectedVersion})" : "✅ Установлено";
                            }
                        }
                        else
                        {
                            failed++;
                            MotionService.Pulse(txtOverallStatus, 1.035, 180);
                        }
                        txtOverallStatus.Text = $"⏳ Установка: {completed + failed}/{appsToInstall.Count} (✅ {completed} | ❌ {failed})";
                    });
                }
                finally { InstallationService.InstallSemaphore.Release(); }
            }, token));

            try
            {
                await Task.WhenAll(tasks);
                txtOverallStatus.Text = $"✅ Установка завершена. Успешно: {completed}, ошибок: {failed}";
                MotionService.Pulse(txtOverallStatus, 1.04, 220);
                appManager.SaveSelectedApps(GetSelectedApps());
                _ = UpdateInstalledStatusAsync();
            }
            catch (OperationCanceledException)
            {
                txtOverallStatus.Text = "⏹️ Установка отменена";
            }
            finally
            {
                _isInstalling = false;
                btnCancelInstall.IsEnabled = false;
                cancellationTokenSource?.Dispose();
                cancellationTokenSource = null;
                UpdateInstallButton();
                UpdateSpaceStatus();
            }
        }

        private void CancelInstall_Click(object sender, RoutedEventArgs e)
        {
            if (cancellationTokenSource != null && !isCancelled)
            {
                var result = MessageBox.Show("Вы действительно хотите прервать установку?", "Подтверждение отмены",
                    MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result == MessageBoxResult.Yes)
                {
                    isCancelled = true;
                    cancellationTokenSource?.Cancel();
                    btnCancelInstall.IsEnabled = false;
                    txtOverallStatus.Text = "⏹️ Установка прервана";
                }
            }
        }

        private void BtnCheckUpdates_Click(object sender, RoutedEventArgs e)
        {
            AddLog("🔔 Переход к управлению обновлениями...");
            SwitchToUpdatesRequested?.Invoke();
        }

        private void BtnExportList_Click(object sender, RoutedEventArgs e)
        {
            var selected = GetSelectedApps();
            if (selected.Count == 0)
            {
                MessageBox.Show("Нет выбранных приложений для экспорта.", "Экспорт",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title      = "Экспорт списка приложений",
                Filter     = "JSON файлы (*.json)|*.json",
                FileName   = $"ven4tools_list_{DateTime.Now:yyyyMMdd_HHmm}.json",
                DefaultExt = ".json"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                var payload = new
                {
                    exported_at = DateTime.Now.ToString("o"),
                    app_ids     = selected.OrderBy(id => id).ToList()
                };
                string json = Newtonsoft.Json.JsonConvert.SerializeObject(payload, Newtonsoft.Json.Formatting.Indented);
                System.IO.File.WriteAllText(dlg.FileName, json, System.Text.Encoding.UTF8);
                AddLog($"📤 Экспорт: {selected.Count} приложений → {System.IO.Path.GetFileName(dlg.FileName)}");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка при сохранении:\n{ex.Message}", "Ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnImportList_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title  = "Импорт списка приложений",
                Filter = "JSON файлы (*.json)|*.json"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                string json = System.IO.File.ReadAllText(dlg.FileName, System.Text.Encoding.UTF8);
                var doc = Newtonsoft.Json.Linq.JObject.Parse(json);
                var ids = doc["app_ids"]?.ToObject<List<string>>()
                       ?? doc["apps"]?.ToObject<List<string>>()
                       ?? new List<string>();

                int matched = 0, skipped = 0;
                foreach (var id in ids)
                {
                    if (appCheckBoxes.TryGetValue(id, out var cb))
                    { cb.IsChecked = true; matched++; }
                    else
                        skipped++;
                }

                AddLog($"📥 Импорт: отмечено {matched}, не найдено в каталоге: {skipped}");
                if (skipped > 0)
                    MessageBox.Show($"Отмечено: {matched}\nНе найдено в текущем каталоге: {skipped}",
                        "Импорт завершён", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка чтения файла:\n{ex.Message}", "Ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static async Task<bool> CreateRestorePointAsync()
        {
            try
            {
                var psi = new ProcessStartInfo("powershell.exe",
                    "-NoProfile -ExecutionPolicy Bypass -Command \"Checkpoint-Computer -Description 'Ven4Tools — перед установкой' -RestorePointType MODIFY_SETTINGS\"")
                {
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true
                };
                using var p = Process.Start(psi);
                if (p == null) return false;
                await Task.WhenAll(
                    p.StandardOutput.ReadToEndAsync(),
                    p.StandardError.ReadToEndAsync());
                await p.WaitForExitAsync();
                return p.ExitCode == 0;
            }
            catch { return false; }
        }
    }
}
