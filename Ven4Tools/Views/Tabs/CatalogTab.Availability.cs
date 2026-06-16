using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Ven4Tools.Models;
using Ven4Tools.Services;

namespace Ven4Tools.Views.Tabs
{
    public partial class CatalogTab
    {
        private void LoadAvailableDisks()
        {
            try
            {
                systemDrive = System.IO.Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') ?? "C:";

                var drives = DriveInfo.GetDrives()
                    .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                    .Select(d => new
                    {
                        Name  = d.RootDirectory.FullName.TrimEnd('\\'),
                        Space = $"{d.Name.TrimEnd('\\')} ({d.AvailableFreeSpace / 1024 / 1024 / 1024:F1} ГБ свободно)"
                    })
                    .ToList();

                cmbAvailableDisks.ItemsSource       = drives;
                cmbAvailableDisks.DisplayMemberPath = "Space";
                cmbAvailableDisks.SelectedValuePath = "Name";

                var systemDisk = drives.FirstOrDefault(d => d.Name == systemDrive);
                if (systemDisk != null)
                {
                    cmbAvailableDisks.SelectedValue = systemDrive;
                    selectedInstallDrive = systemDrive + "\\";
                }
                else if (drives.Any())
                {
                    cmbAvailableDisks.SelectedIndex = 0;
                    selectedInstallDrive = drives.First().Name + "\\";
                }

                UpdateDiskSpaceInfo();
            }
            catch (Exception ex)
            {
                AddLog($"⚠️ Ошибка получения списка дисков: {ex.Message}");
            }
        }

        private void CmbAvailableDisks_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cmbAvailableDisks.SelectedValue != null)
            {
                selectedInstallDrive = cmbAvailableDisks.SelectedValue.ToString() + "\\";
                UpdateDiskSpaceInfo();
                UpdateSpaceStatus();
            }
        }

        private void UpdateDiskSpaceInfo()
        {
            try
            {
                string disk = selectedInstallDrive.TrimEnd('\\');
                var drive = new DriveInfo(disk);
                if (drive.IsReady)
                {
                    long freeSpaceGB  = drive.AvailableFreeSpace / 1024 / 1024 / 1024;
                    long totalSpaceGB = drive.TotalSize / 1024 / 1024 / 1024;
                    txtSpaceStatus.Text = $"💾 Диск {disk} | Свободно: {freeSpaceGB} ГБ / {totalSpaceGB} ГБ";
                }
            }
            catch (Exception ex)
            {
                AddLog($"⚠️ Ошибка обновления информации о диске: {ex.Message}");
            }
        }

        private async Task CheckAppAvailabilityFromCatalog(Models.App catalogApp)
        {
            try
            {
                AppInfo? appInfo = appManager.GetAppById(catalogApp.Id);

                if (appInfo == null)
                {
                    appInfo = new AppInfo
                    {
                        Id = catalogApp.Id,
                        DisplayName = catalogApp.Name,
                        Category = GetCategoryFromString(catalogApp.Category),
                        InstallerUrls = !string.IsNullOrEmpty(catalogApp.DownloadUrl)
                            ? new List<string> { catalogApp.DownloadUrl }
                            : new List<string>(),
                        AlternativeId = catalogApp.WingetId,
                        SilentArgs = "/S",
                        RequiredSpaceMB = ParseSizeToMB(catalogApp.Size),
                        IsUserAdded = false,
                        ChocoId = catalogApp.ChocoId,
                        ScoopId = catalogApp.ScoopId
                    };
                }

                var availabilityResult = await availabilityChecker.CheckAppAvailabilityWithSize(appInfo);
                var status = availabilityResult.Status;
                var size   = availabilityResult.SizeMB;

                await Dispatcher.InvokeAsync(() =>
                {
                    if (appCheckBoxes.TryGetValue(catalogApp.Id, out var checkBox))
                    {
                        availabilityStatus[catalogApp.Id] = status;

                        if (checkBox.Content is TextBlock tb)
                        {
                            switch (status)
                            {
                                case AvailabilityChecker.AvailabilityStatus.Available:
                                    tb.Foreground    = Brushes.LightGreen;
                                    checkBox.ToolTip = $"✅ Доступно для установки ({(size > 0 ? $"~{size} МБ" : "размер неизвестен")})";
                                    checkBox.IsEnabled = true;
                                    break;
                                case AvailabilityChecker.AvailabilityStatus.Unavailable:
                                    tb.Foreground    = Brushes.LightCoral;
                                    checkBox.ToolTip = "❌ Недоступно";
                                    checkBox.IsEnabled = false;
                                    AddSuggestionButton(catalogApp, checkBox);
                                    break;
                                default:
                                    tb.Foreground    = Brushes.Gray;
                                    checkBox.ToolTip = "⚠️ Статус неизвестен";
                                    checkBox.IsEnabled = true;
                                    break;
                            }
                        }
                        checkBox.InvalidateVisual();
                    }
                });
            }
            catch (Exception ex)
            {
                AddLog($"❌ Ошибка при проверке {catalogApp.Name}: {ex.Message}");
            }
        }

        private void AddSuggestionButton(Models.App catalogApp, CheckBox checkBox)
        {
            try
            {
                // checkBox lives inside a row StackPanel; that row lives in the category StackPanel
                var rowPanel = System.Windows.Media.VisualTreeHelper.GetParent(checkBox) as StackPanel;
                if (rowPanel == null) return;

                var existingButton = rowPanel.Children.OfType<Button>().FirstOrDefault(b => b.Tag?.ToString() == catalogApp.Id + "_suggest");
                if (existingButton != null) return;

                var suggestButton = new Button
                {
                    Content = "🔄",
                    Width = 24,
                    Height = 24,
                    Tag = catalogApp.Id + "_suggest",
                    Background = new SolidColorBrush(Color.FromRgb(255, 140, 0)),
                    Foreground = Brushes.White,
                    FontSize = 12,
                    ToolTip = "Предложить альтернативный источник",
                    Cursor = Cursors.Hand,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(5, 0, 0, 0)
                };
                suggestButton.Click += async (s, e) => await SuggestAlternativeForCatalog(catalogApp);
                rowPanel.Children.Add(suggestButton);
            }
            catch (Exception ex)
            {
                AddLog($"❌ Ошибка добавления кнопки альтернативы: {ex.Message}");
            }
        }

        private async Task SuggestAlternativeForCatalog(Models.App catalogApp)
        {
            try
            {
                AddLog($"🔍 Поиск альтернативы для: {catalogApp.Name}");
                var dialog = new AlternativeSourceDialog(catalogApp.Name) { Owner = Window.GetWindow(this) };

                if (dialog.ShowDialog() == true)
                {
                    if (dialog.SelectedPackage != null)
                    {
                        appManager.SaveAlternativeSource(catalogApp.Id, dialog.SelectedPackage.Id, null, dialog.UseWingetFirst);
                        AddLog($"✅ Сохранён Winget ID: {dialog.SelectedPackage.Id} для {catalogApp.Name}");
                    }
                    else if (!string.IsNullOrEmpty(dialog.CustomUrl))
                    {
                        appManager.SaveAlternativeSource(catalogApp.Id, null, dialog.CustomUrl, dialog.UseUrlFirst);
                        AddLog($"✅ Сохранена ссылка: {dialog.CustomUrl} для {catalogApp.Name}");
                    }
                    await Task.Delay(500);
                    await CheckAppAvailabilityFromCatalog(catalogApp);
                }
            }
            catch (Exception ex)
            {
                AddLog($"❌ Ошибка: {ex.Message}");
            }
        }

        private async Task CheckSingleAppAvailability(string appId, int attempt = 1)
        {
            try
            {
                var app = appManager.GetAllApps().FirstOrDefault(a => a.Id == appId);
                if (app == null) return;

                var availabilityResult = await availabilityChecker.CheckAppAvailabilityWithSize(app);
                var status = availabilityResult.Status;
                var size   = availabilityResult.SizeMB;

                await Dispatcher.InvokeAsync(() =>
                {
                    if (appCheckBoxes.TryGetValue(appId, out var checkBox) && checkBox.Content is TextBlock tb)
                    {
                        availabilityStatus[appId] = status;

                        switch (status)
                        {
                            case AvailabilityChecker.AvailabilityStatus.Available:
                                tb.Foreground    = Brushes.LightGreen;
                                checkBox.ToolTip = $"✅ Доступно для установки ({(size > 0 ? $"~{size} МБ" : "размер неизвестен")})";
                                checkBox.IsEnabled = true;
                                break;
                            case AvailabilityChecker.AvailabilityStatus.Unavailable:
                                if (attempt < 3)
                                {
                                    tb.Foreground    = Brushes.Gray;
                                    checkBox.ToolTip = $"⏳ Повторная проверка... ({attempt}/3)";
                                    // Ретраи проверки доступности независимы от установки:
                                    // токен установки отменял бы и несвязанные проверки.
                                    // Используем выделенный токен, который гасится только
                                    // при закрытии окна приложения.
                                    var token = _availabilityCts.Token;
                                    _ = Task.Delay(2000, token).ContinueWith(
                                        t => { if (!t.IsCanceled) return CheckSingleAppAvailability(appId, attempt + 1); return Task.CompletedTask; },
                                        TaskScheduler.Default).Unwrap();
                                }
                                else
                                {
                                    tb.Foreground    = Brushes.LightCoral;
                                    checkBox.ToolTip = "❌ Недоступно";
                                    checkBox.IsEnabled = false;
                                }
                                break;
                            default:
                                tb.Foreground    = Brushes.Gray;
                                checkBox.ToolTip = "⚠️ Статус неизвестен";
                                checkBox.IsEnabled = true;
                                break;
                        }
                        checkBox.InvalidateVisual();
                    }
                });
            }
            catch (Exception ex)
            {
                AddLog($"❌ Ошибка при проверке {appId}: {ex.Message}");
            }
        }

        private async void RefreshAvailability_Click(object sender, RoutedEventArgs e)
        {
            if (_isCheckingAvailability) return;
            _isCheckingAvailability = true;
            btnRefreshAvailability.IsEnabled = false;
            availabilityChecker.ClearCache();
            availabilityStatus.Clear();

            AddLog("🔄 Запущена свежая проверка доступности...");

            if (_catalog != null)
                foreach (var app in _catalog.Apps)
                    if (appCheckBoxes.TryGetValue(app.Id, out var cb) && cb.Content is TextBlock tb)
                    { tb.Foreground = Brushes.Gray; cb.ToolTip = "⏳ Проверка..."; cb.IsEnabled = true; }

            foreach (var app in appManager.GetAllApps().Where(a => a.IsUserAdded))
                if (appCheckBoxes.TryGetValue(app.Id, out var cb) && cb.Content is TextBlock tb)
                { tb.Foreground = Brushes.Gray; cb.ToolTip = "⏳ Проверка..."; cb.IsEnabled = true; }

            // Throttle: max 5 concurrent winget show calls
            var sem   = new SemaphoreSlim(5);
            var tasks = new List<Task>();

            try
            {
                if (_catalog != null)
                    foreach (var app in _catalog.Apps)
                    {
                        var localApp = app;
                        tasks.Add(Task.Run(async () =>
                        {
                            await sem.WaitAsync();
                            try { await CheckAppAvailabilityFromCatalog(localApp); }
                            finally { sem.Release(); }
                        }));
                    }

                foreach (var app in appManager.GetAllApps().Where(a => a.IsUserAdded))
                {
                    var localId = app.Id;
                    tasks.Add(Task.Run(async () =>
                    {
                        await sem.WaitAsync();
                        try { await CheckSingleAppAvailability(localId); }
                        finally { sem.Release(); }
                    }));
                }

                await Task.WhenAll(tasks);

                int available   = availabilityStatus.Values.Count(s => s == AvailabilityChecker.AvailabilityStatus.Available);
                int unavailable = availabilityStatus.Values.Count(s => s == AvailabilityChecker.AvailabilityStatus.Unavailable);
                AddLog($"✅ Проверка завершена: {available} доступно, {unavailable} недоступно");
            }
            catch (Exception ex)
            {
                AddLog($"⚠️ Ошибка проверки доступности: {ex.Message}");
            }
            finally
            {
                btnRefreshAvailability.IsEnabled = true;
                _isCheckingAvailability = false;
            }
        }

        private async void UpdateSpaceStatus()
        {
            try
            {
                var selected = GetSelectedApps();
                long totalRequired = 0;
                foreach (var appId in selected)
                {
                    var app = appManager.GetAppById(appId);
                    if (app != null)
                    {
                        var result = await availabilityChecker.CheckAppAvailabilityWithSize(app);
                        totalRequired += result.Status == AvailabilityChecker.AvailabilityStatus.Available ? result.SizeMB : 100;
                    }
                }
                string disk  = selectedInstallDrive.TrimEnd('\\');
                var drive    = new DriveInfo(disk);
                if (drive.IsReady)
                {
                    long availableMB = drive.AvailableFreeSpace / 1024 / 1024;
                    if (availableMB >= totalRequired)
                    {
                        txtSpaceStatus.Text       = $"💾 Диск {disk} | Требуется: ~{totalRequired} МБ | Доступно: {availableMB} МБ ✅";
                        txtSpaceStatus.Foreground = new SolidColorBrush(Color.FromRgb(0, 255, 0));
                    }
                    else
                    {
                        txtSpaceStatus.Text       = $"💾 Диск {disk} | Требуется: ~{totalRequired} МБ | Доступно: {availableMB} МБ ❌ Мало места!";
                        txtSpaceStatus.Foreground = new SolidColorBrush(Color.FromRgb(255, 100, 100));
                    }
                }
            }
            catch (Exception ex) { AddLog($"⚠️ Ошибка проверки места: {ex.Message}"); }
        }

        private async Task UpdateInstalledStatusAsync()
        {
            await installedAppsService.RefreshAsync();

            int installedCount = 0;
            int outdatedCount  = 0;

            await Dispatcher.InvokeAsync(() =>
            {
                foreach (var kvp in appCheckBoxes)
                {
                    var appInfo = appManager.GetAppById(kvp.Key);
                    string wingetId = !string.IsNullOrEmpty(appInfo?.AlternativeId)
                        ? appInfo!.AlternativeId
                        : kvp.Key;

                    bool isInstalled = installedAppsService.IsInstalled(wingetId);
                    // Помечаем состояние установки на чекбоксе явным флагом —
                    // ApplyHideInstalled полагается на него, а не на текст тултипа.
                    CatalogTab.SetInstalled(kvp.Value, isInstalled);

                    if (isInstalled && kvp.Value.Content is TextBlock tb)
                    {
                        string version = installedAppsService.GetInstalledVersion(wingetId);
                        bool hasUpdate = false;
                        if (!string.IsNullOrEmpty(version) && _appVersions.TryGetValue(kvp.Key, out var knownVersions) && knownVersions.Count > 0)
                            hasUpdate = version != knownVersions[0];

                        if (hasUpdate)
                        {
                            tb.Foreground    = new SolidColorBrush(Color.FromRgb(255, 165, 0));
                            kvp.Value.ToolTip = $"✓ Установлено ({version}) | 🆙 Доступна новая версия";
                            outdatedCount++;
                        }
                        else
                        {
                            tb.Foreground    = new SolidColorBrush(Color.FromRgb(100, 149, 237));
                            kvp.Value.ToolTip = string.IsNullOrEmpty(version)
                                ? "✓ Уже установлено"
                                : $"✓ Установлено ({version})";
                        }
                        installedCount++;
                    }
                }
            });

            if (installedCount > 0)
                AddLog($"📦 Уже установлено: {installedCount} из {appCheckBoxes.Count} приложений");
            if (outdatedCount > 0)
                AddLog($"🆙 Доступно обновлений: {outdatedCount}");

            if (ProfileService.Current.HideInstalled)
                ApplyHideInstalled();
        }

        private void ApplyHideInstalled()
        {
            foreach (var kvp in appCheckBoxes)
            {
                if (GetInstalled(kvp.Value))
                {
                    var row = System.Windows.Media.VisualTreeHelper.GetParent(kvp.Value) as FrameworkElement ?? kvp.Value;
                    row.Visibility = Visibility.Collapsed;
                }
            }
        }

        // Attached property: хранит признак «приложение установлено» на чекбоксе.
        // Заменяет хрупкую проверку текста тултипа в ApplyHideInstalled.
        public static readonly DependencyProperty IsInstalledProperty =
            DependencyProperty.RegisterAttached(
                "IsInstalled",
                typeof(bool),
                typeof(CatalogTab),
                new PropertyMetadata(false));

        public static void SetInstalled(DependencyObject element, bool value) =>
            element.SetValue(IsInstalledProperty, value);

        public static bool GetInstalled(DependencyObject element) =>
            (bool)element.GetValue(IsInstalledProperty);

    }
}
