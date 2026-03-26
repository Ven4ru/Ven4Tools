using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using Microsoft.Win32;
using Ven4Tools.Models;
using Ven4Tools.Services;

namespace Ven4Tools.Views.Tabs
{
    public partial class CatalogTab : UserControl
    {
        private readonly string[] wingetSources = { "winget", "msstore", "custom" };
        private readonly SemaphoreSlim installSemaphore = new SemaphoreSlim(2);
        private bool isCancelled = false;
        private CancellationTokenSource? cancellationTokenSource;
        private readonly AppManager appManager = null!;
        private readonly InstallationService installService = null!;
        private readonly AvailabilityChecker availabilityChecker = null!;
        private Dictionary<string, CheckBox> appCheckBoxes = new();
        private Dictionary<AppCategory, StackPanel> categoryPanels = new();
        private Dictionary<string, AvailabilityChecker.AvailabilityStatus> availabilityStatus = new();
        private MasterCatalog? _catalog;
        private CatalogLoaderService? _catalogLoader;
        private string selectedInstallDrive = "C:\\";
        private string systemDrive = "C:\\";
        
        public event Action<string>? LogMessage;
        
        public CatalogTab()
        {
            InitializeComponent();
            
            _catalogLoader = new CatalogLoaderService();
            appManager = new AppManager();
            installService = new InstallationService();
            availabilityChecker = new AvailabilityChecker();
            
            InitCategoryPanels();
            LoadAvailableDisks();
            
            Loaded += async (s, e) => await LoadCatalogAndRefreshAsync();
            
            btnInstall.Click += InstallSelected_Click;
            btnCancelInstall.Click += CancelInstall_Click;
            btnAddApp.Click += BtnAddApp_Click;
            btnClearAllUserApps.Click += BtnClearAllUserApps_Click;
            btnRefreshCatalog.Click += BtnRefreshCatalog_Click;
            btnRefreshAvailability.Click += RefreshAvailability_Click;
            btnSuggestAlternatives.Click += BtnSuggestAlternatives_Click;
            cmbAvailableDisks.SelectionChanged += CmbAvailableDisks_SelectionChanged;
        }
        
        private void InitCategoryPanels()
        {
            categoryPanels[AppCategory.Браузеры] = PanelБраузеры;
            categoryPanels[AppCategory.Офис] = PanelОфис;
            categoryPanels[AppCategory.Графика] = PanelГрафика;
            categoryPanels[AppCategory.Разработка] = PanelРазработка;
            categoryPanels[AppCategory.Мессенджеры] = PanelМессенджеры;
            categoryPanels[AppCategory.Мультимедиа] = PanelМультимедиа;
            categoryPanels[AppCategory.Системные] = PanelСистемные;
            categoryPanels[AppCategory.ИгровыеСервисы] = PanelИгровыеСервисы;
            categoryPanels[AppCategory.Драйверпаки] = PanelДрайверпаки;
            categoryPanels[AppCategory.Другое] = PanelДругое;
            categoryPanels[AppCategory.Пользовательские] = PanelПользовательские;
        }
        
        private void LoadAvailableDisks()
        {
            try
            {
                systemDrive = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') ?? "C:";

                var drives = DriveInfo.GetDrives()
                    .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                    .Select(d => new
                    {
                        Name = d.RootDirectory.FullName.TrimEnd('\\'),
                        Space = $"{d.Name.TrimEnd('\\')} ({d.AvailableFreeSpace / 1024 / 1024 / 1024:F1} ГБ свободно)"
                    })
                    .ToList();

                cmbAvailableDisks.ItemsSource = drives;
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
                    long freeSpaceGB = drive.AvailableFreeSpace / 1024 / 1024 / 1024;
                    long totalSpaceGB = drive.TotalSize / 1024 / 1024 / 1024;
                    txtSpaceStatus.Text = $"💾 Диск {disk} | Свободно: {freeSpaceGB} ГБ / {totalSpaceGB} ГБ";
                }
            }
            catch (Exception ex)
            {
                AddLog($"⚠️ Ошибка обновления информации о диске: {ex.Message}");
            }
        }
        
        private async Task LoadCatalogAndRefreshAsync()
        {
            try
            {
                if (_catalogLoader == null) return;

                var loadedCatalog = await _catalogLoader.LoadCatalogAsync();
                
                if (loadedCatalog == null)
                {
                    AddLog("❌ Ошибка: не удалось загрузить каталог");
                    return;
                }
                
                _catalog = loadedCatalog;
                SyncCatalogToAppManager();
                
                string sourceText = _catalog.Source switch
                {
                    "online" => "🌐 Каталог загружен из интернета",
                    "cache" => "💾 Каталог из кэша (интернет недоступен)",
                    "embedded" => "📀 Встроенный каталог (минимальный набор)",
                    _ => "❓ Неизвестный источник"
                };

                AddLog(sourceText);
                AddLog($"Загружено приложений: {_catalog.Apps.Count}");

                LoadApps();
            }
            catch (Exception ex)
            {
                AddLog($"Ошибка загрузки каталога: {ex.Message}");
            }
        }
        
        private void LoadApps()
        {
            try
            {
                foreach (var panel in categoryPanels.Values)
                {
                    panel.Children.Clear();
                }
                appCheckBoxes.Clear();
                availabilityStatus.Clear();

                if (_catalog == null)
                {
                    AddLog("⚠️ Каталог не загружен");
                    return;
                }

                AddLog($"Каталог содержит {_catalog.Apps.Count} приложений");

                foreach (var app in _catalog.Apps)
                {
                    var category = GetCategoryFromString(app.Category);

                    if (categoryPanels.TryGetValue(category, out var panel) && panel != null)
                    {
                        var textBlock = new TextBlock
                        {
                            Text = app.Name,
                            Foreground = Brushes.Gray,
                            Margin = new Thickness(0, 0, 5, 0)
                        };
                        
                        var checkBox = new CheckBox
                        {
                            Content = textBlock,
                            Tag = app.Id,
                            Margin = new Thickness(0, 2, 0, 2),
                            ToolTip = "⏳ Проверка доступности..."
                        };

                        panel.Children.Add(checkBox);
                        appCheckBoxes[app.Id] = checkBox;
                        _ = CheckAppAvailabilityFromCatalog(app);
                    }
                }

                var userApps = appManager.GetAllApps().Where(a => a.IsUserAdded).ToList();
                foreach (var app in userApps)
                {
                    AddUserAppToUI(app);
                }

                AddLog($"Загружено {_catalog.Apps.Count} приложений из каталога, {userApps.Count} пользовательских");
            }
            catch (Exception ex)
            {
                AddLog($"Ошибка при загрузке приложений: {ex.Message}");
            }
        }
        
        private void AddUserAppToUI(AppInfo app)
        {
            var textBlock = new TextBlock
            {
                Text = app.DisplayName,
                Foreground = new SolidColorBrush(Color.FromRgb(255, 140, 0)),
                Margin = new Thickness(0, 0, 5, 0)
            };
            
            var checkBox = new CheckBox
            {
                Content = textBlock,
                Tag = app.Id,
                Margin = new Thickness(0, 2, 0, 2),
                ToolTip = "👤 Пользовательское приложение"
            };

            var removeButton = new Button
            {
                Content = "❌",
                Width = 20,
                Height = 20,
                Margin = new Thickness(5, 0, 0, 0),
                Tag = app.Id,
                Background = new SolidColorBrush(Color.FromRgb(100, 0, 0)),
                Foreground = Brushes.White,
                FontSize = 10,
                Padding = new Thickness(0),
                ToolTip = "Удалить из списка"
            };
            removeButton.Click += (s, args) => RemoveUserApp(app.Id);

            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(checkBox);
            panel.Children.Add(removeButton);

            PanelПользовательские.Children.Add(panel);
            appCheckBoxes[app.Id] = checkBox;
            availabilityStatus[app.Id] = AvailabilityChecker.AvailabilityStatus.Available;
        }
        
        private void RemoveUserApp(string appId)
        {
            var result = MessageBox.Show("Удалить приложение из списка?", "Подтверждение",
                MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    appManager.RemoveUserApp(appId);

                    var toRemove = PanelПользовательские.Children
                        .OfType<StackPanel>()
                        .FirstOrDefault(sp => sp.Children
                            .OfType<CheckBox>()
                            .Any(c => c.Tag?.ToString() == appId));

                    if (toRemove != null)
                    {
                        PanelПользовательские.Children.Remove(toRemove);
                    }

                    appCheckBoxes.Remove(appId);
                    availabilityStatus.Remove(appId);
                    AddLog($"🗑️ Удалено пользовательское приложение");
                }
                catch (Exception ex)
                {
                    AddLog($"❌ Ошибка при удалении: {ex.Message}");
                }
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
                        IsUserAdded = false
                    };
                }

                var availabilityResult = await availabilityChecker.CheckAppAvailabilityWithSize(appInfo);
                var status = availabilityResult.Status;
                var size = availabilityResult.SizeMB;

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
                                    tb.Foreground = Brushes.LightGreen;
                                    checkBox.ToolTip = $"✅ Доступно для установки ({(size > 0 ? $"~{size} МБ" : "размер неизвестен")})";
                                    checkBox.IsEnabled = true;
                                    break;
                                case AvailabilityChecker.AvailabilityStatus.Unavailable:
                                    tb.Foreground = Brushes.LightCoral;
                                    checkBox.ToolTip = "❌ Недоступно";
                                    checkBox.IsEnabled = false;
                                    AddSuggestionButton(catalogApp, checkBox);
                                    break;
                                default:
                                    tb.Foreground = Brushes.Gray;
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
                var parentPanel = VisualTreeHelper.GetParent(checkBox) as StackPanel;
                if (parentPanel == null) return;

                var existingButton = parentPanel.Children.OfType<Button>().FirstOrDefault(b => b.Tag?.ToString() == catalogApp.Id + "_suggest");
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

                int index = parentPanel.Children.IndexOf(checkBox);
                parentPanel.Children.RemoveAt(index);
                
                var newRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
                newRow.Children.Add(checkBox);
                newRow.Children.Add(suggestButton);
                parentPanel.Children.Insert(index, newRow);
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
                var size = availabilityResult.SizeMB;

                await Dispatcher.InvokeAsync(() =>
                {
                    if (appCheckBoxes.TryGetValue(appId, out var checkBox) && checkBox.Content is TextBlock tb)
                    {
                        availabilityStatus[appId] = status;

                        switch (status)
                        {
                            case AvailabilityChecker.AvailabilityStatus.Available:
                                tb.Foreground = Brushes.LightGreen;
                                checkBox.ToolTip = $"✅ Доступно для установки ({(size > 0 ? $"~{size} МБ" : "размер неизвестен")})";
                                checkBox.IsEnabled = true;
                                break;
                            case AvailabilityChecker.AvailabilityStatus.Unavailable:
                                if (attempt < 3)
                                {
                                    tb.Foreground = Brushes.Gray;
                                    checkBox.ToolTip = $"⏳ Повторная проверка... ({attempt}/3)";
                                    _ = Task.Delay(2000).ContinueWith(_ => CheckSingleAppAvailability(appId, attempt + 1));
                                }
                                else
                                {
                                    tb.Foreground = Brushes.LightCoral;
                                    checkBox.ToolTip = "❌ Недоступно";
                                    checkBox.IsEnabled = false;
                                }
                                break;
                            default:
                                tb.Foreground = Brushes.Gray;
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
            AddLog("🔄 Перезапущена проверка доступности");

            if (_catalog != null)
            {
                foreach (var app in _catalog.Apps)
                {
                    if (availabilityStatus.ContainsKey(app.Id))
                        availabilityStatus.Remove(app.Id);

                    if (appCheckBoxes.TryGetValue(app.Id, out var checkBox) && checkBox.Content is TextBlock tb)
                    {
                        tb.Foreground = Brushes.Gray;
                        checkBox.ToolTip = "⏳ Проверка...";
                        checkBox.IsEnabled = true;
                    }
                    _ = CheckAppAvailabilityFromCatalog(app);
                }
            }

            var userApps = appManager.GetAllApps().Where(a => a.IsUserAdded).ToList();
            foreach (var app in userApps)
            {
                if (availabilityStatus.ContainsKey(app.Id))
                    availabilityStatus.Remove(app.Id);

                if (appCheckBoxes.TryGetValue(app.Id, out var checkBox) && checkBox.Content is TextBlock tb)
                {
                    tb.Foreground = Brushes.Gray;
                    checkBox.ToolTip = "⏳ Проверка...";
                    checkBox.IsEnabled = true;
                }
                _ = CheckSingleAppAvailability(app.Id);
            }

            AddLog("✅ Проверка запущена для всех приложений");
        }
        
        private async void BtnSuggestAlternatives_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var unavailable = new List<(string AppId, string DisplayName, string OriginalId, string OriginalUrl, bool IsUserAdded)>();
                foreach (var kvp in availabilityStatus)
                {
                    if (kvp.Value == AvailabilityChecker.AvailabilityStatus.Unavailable)
                    {
                        var app = appManager.GetAppById(kvp.Key);
                        if (app != null)
                            unavailable.Add((kvp.Key, app.DisplayName, app.Id, app.InstallerUrls.FirstOrDefault() ?? "", app.IsUserAdded));
                    }
                }

                if (unavailable.Count == 0)
                {
                    MessageBox.Show("Нет недоступных приложений.", "Информация", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                AddLog($"Найдено {unavailable.Count} недоступных приложений");
                var dialog = new BulkFallbackDialog(unavailable) { Owner = Window.GetWindow(this) };

                if (dialog.ShowDialog() == true && dialog.Applied)
                {
                    foreach (var item in dialog.FailedApps.Where(x => x.SearchWinget && x.SelectedPackage != null))
                        appManager.SaveAlternativeSource(item.AppId, item.SelectedPackage.Id, null);
                    foreach (var item in dialog.FailedApps.Where(x => x.ReplaceWithUrl && !string.IsNullOrWhiteSpace(x.NewUrl)))
                        appManager.SaveAlternativeSource(item.AppId, null, item.NewUrl);

                    AddLog("Повторная проверка доступности...");
                    if (_catalog != null)
                    {
                        foreach (var app in _catalog.Apps)
                            await CheckAppAvailabilityFromCatalog(app);
                    }
                    AddLog("Готово");
                }
            }
            catch (Exception ex) { AddLog($"Ошибка: {ex.Message}"); }
        }
        
        private async void InstallSelected_Click(object sender, RoutedEventArgs e)
        {
            var selectedApps = GetSelectedApps();
            if (selectedApps.Count == 0)
            {
                MessageBox.Show("Выберите хотя бы одну программу!", "Ven4Tools", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            lstAppProgress.Items.Clear();
            installProgressBar.Value = 0;
            isCancelled = false;
            btnInstall.IsEnabled = false;
            btnCancelInstall.IsEnabled = true;
            cancellationTokenSource = new CancellationTokenSource();
            var token = cancellationTokenSource.Token;

            var allApps = appManager.GetAllApps();
            var appsToInstall = allApps.Where(a => selectedApps.Contains(a.Id)).ToList();
            var progressDict = new Dictionary<string, AppInstallProgress>();

            AddLog($"💾 Установка на диск: {selectedInstallDrive}");

            var progress = new Progress<AppInstallProgress>(p =>
            {
                progressDict[p.AppId] = p;
                var existing = lstAppProgress.Items.OfType<AppInstallProgress>().FirstOrDefault(x => x.AppId == p.AppId);
                if (existing != null)
                {
                    existing.Status = p.Status;
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

            var tasks = appsToInstall.Select(app => Task.Run(async () =>
            {
                await installSemaphore.WaitAsync();
                try
                {
                    if (token.IsCancellationRequested) return;
                    var result = await installService.InstallAppAsync(app, wingetSources, token, progress, selectedInstallDrive);
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (result.Success)
                        {
                            completed++;
                            if (appCheckBoxes.TryGetValue(app.Id, out var cb) && cb.Content is TextBlock tb)
                            {
                                tb.Foreground = new SolidColorBrush(Color.FromRgb(136, 136, 136));
                                cb.Opacity = 0.7;
                                cb.IsEnabled = false;
                                cb.ToolTip = "✅ Установлено";
                            }
                        }
                        else failed++;
                        txtOverallStatus.Text = $"⏳ Установка: {completed + failed}/{appsToInstall.Count} (✅ {completed} | ❌ {failed})";
                    });
                }
                finally { installSemaphore.Release(); }
            }, token));

            try
            {
                await Task.WhenAll(tasks);
                txtOverallStatus.Text = $"✅ Установка завершена. Успешно: {completed}, ошибок: {failed}";
                appManager.SaveSelectedApps(GetSelectedApps());
            }
            catch (OperationCanceledException)
            {
                txtOverallStatus.Text = "⏹️ Установка отменена";
            }
            finally
            {
                btnInstall.IsEnabled = true;
                btnCancelInstall.IsEnabled = false;
                cancellationTokenSource?.Dispose();
                cancellationTokenSource = null;
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
        
        private async void BtnAddApp_Click(object sender, RoutedEventArgs e)
        {
            var consentService = new ConsentService();
            var shouldAsk = await consentService.ShouldAskForConsentAsync();
            bool allowStats = true;

            if (shouldAsk)
            {
                var dialog = new Views.ConsentDialog();
                if (dialog.ShowDialog() == true)
                {
                    allowStats = dialog.AllowStats;
                    await consentService.SaveConsentAsync(allowStats);
                }
                else return;
            }
            else allowStats = await consentService.IsStatsAllowedAsync();

            var addDialog = new Services.AddAppDialog();
            if (addDialog.ShowDialog() == true && addDialog.Result != null)
            {
                var newApp = addDialog.Result;
                appManager.AddUserApp(newApp);
                AddUserAppToUI(newApp);

                if (allowStats)
                {
                    string? wingetId = null;
                    if (_catalog != null)
                    {
                        var catalogApp = _catalog.Apps.FirstOrDefault(a => a.Name == newApp.DisplayName);
                        if (catalogApp != null && !string.IsNullOrEmpty(catalogApp.WingetId))
                            wingetId = catalogApp.WingetId;
                    }
                    if (string.IsNullOrEmpty(wingetId) && !string.IsNullOrEmpty(newApp.AlternativeId))
                        wingetId = newApp.AlternativeId;
                    await StatsService.Instance.TrackUserAddAsync(newApp.Id, wingetId, newApp.InstallerUrls.FirstOrDefault());
                }
                AddLog($"➕ Добавлено пользовательское приложение: {newApp.DisplayName}");
            }
        }
        
        private async void BtnClearAllUserApps_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show("Вы действительно хотите удалить ВСЕ пользовательские приложения?", "Полная очистка",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    string configPath;
                    string appDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ven4Tools");
                    string exeDir = AppDomain.CurrentDomain.BaseDirectory;
                    string portableMarker = Path.Combine(exeDir, "portable.dat");
                    configPath = File.Exists(portableMarker) ? Path.Combine(exeDir, "Data", "apps.json") : Path.Combine(appDataPath, "apps.json");
                    if (File.Exists(configPath)) File.Delete(configPath);
                    string selectionPath = configPath + ".selection";
                    if (File.Exists(selectionPath)) File.Delete(selectionPath);
                    AddLog($"✅ Файл пользовательских приложений очищен");
                    ReloadAllApps();
                }
                catch (Exception ex) { AddLog($"❌ Ошибка при удалении: {ex.Message}"); }
            }
        }
        
        private void ReloadAllApps()
        {
            try
            {
                PanelБраузеры.Children.Clear(); PanelОфис.Children.Clear(); PanelГрафика.Children.Clear();
                PanelРазработка.Children.Clear(); PanelМессенджеры.Children.Clear(); PanelМультимедиа.Children.Clear();
                PanelСистемные.Children.Clear(); PanelИгровыеСервисы.Children.Clear(); PanelДрайверпаки.Children.Clear();
                PanelДругое.Children.Clear(); PanelПользовательские.Children.Clear();
                appCheckBoxes.Clear(); availabilityStatus.Clear();
                LoadApps();
                AddLog("🔄 Список приложений перезагружен");
            }
            catch (Exception ex) { AddLog($"❌ Ошибка при перезагрузке: {ex.Message}"); }
        }
        
        private async void BtnRefreshCatalog_Click(object sender, RoutedEventArgs e)
        {
            AddLog("🔄 Обновление каталога с GitHub...");
            try
            {
                var url = "https://raw.githubusercontent.com/Ven4ru/Ven4Tools/main/Catalog/master.json";
                AddLog($"📡 URL: {url}");
                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromSeconds(5);
                client.DefaultRequestHeaders.Add("User-Agent", "Ven4Tools");
                var response = await client.GetAsync(url);
                AddLog($"📡 HTTP статус: {response.StatusCode}");
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    AddLog($"📦 Размер JSON: {json.Length} байт");
                    var dateMatch = System.Text.RegularExpressions.Regex.Match(json, @"""lastUpdated"":\s*""([^""]+)""");
                    if (dateMatch.Success) AddLog($"📅 Версия на GitHub: {dateMatch.Groups[1].Value}");
                }
                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var catalogCachePath = Path.Combine(localAppData, "Ven4Tools", "catalog_cache.json");
                if (File.Exists(catalogCachePath)) File.Delete(catalogCachePath);
                if (_catalogLoader == null) { AddLog("❌ Ошибка: загрузчик каталога не инициализирован"); return; }
                var loadedCatalog = await _catalogLoader.LoadCatalogAsync();
                if (loadedCatalog == null) { AddLog("❌ Ошибка: не удалось загрузить каталог"); return; }
                _catalog = loadedCatalog;
                SyncCatalogToAppManager();
                appManager.LoadAlternativeSources();
                appManager.ApplyAlternativesToCatalog(_catalog);
                AddLog($"📦 Загружено приложений: {_catalog.Apps.Count}");
                LoadApps();
                AddLog("✅ Каталог успешно обновлён");
            }
            catch (Exception ex) { AddLog($"❌ Ошибка: {ex.Message}"); }
        }
        
        private void SyncCatalogToAppManager()
        {
            if (_catalog == null) return;
            foreach (var catalogApp in _catalog.Apps)
            {
                var existing = appManager.GetAppById(catalogApp.Id);
                if (existing == null)
                {
                    var appInfo = new AppInfo
                    {
                        Id = catalogApp.Id,
                        DisplayName = catalogApp.Name,
                        Category = GetCategoryFromString(catalogApp.Category),
                        InstallerUrls = !string.IsNullOrEmpty(catalogApp.DownloadUrl) ? new List<string> { catalogApp.DownloadUrl } : new List<string>(),
                        AlternativeId = catalogApp.WingetId,
                        RequiredSpaceMB = ParseSizeToMB(catalogApp.Size),
                        IsUserAdded = false
                    };
                    appManager.AddCatalogApp(appInfo);
                }
            }
        }
        
        private AppCategory GetCategoryFromString(string category)
        {
            return category switch
            {
                "Браузеры" => AppCategory.Браузеры,
                "Офис" => AppCategory.Офис,
                "Графика" => AppCategory.Графика,
                "Разработка" => AppCategory.Разработка,
                "Мессенджеры" => AppCategory.Мессенджеры,
                "Мультимедиа" => AppCategory.Мультимедиа,
                "Системные" => AppCategory.Системные,
                "Игровые сервисы" => AppCategory.ИгровыеСервисы,
                "Драйверпаки" => AppCategory.Драйверпаки,
                _ => AppCategory.Другое
            };
        }
        
        private int ParseSizeToMB(string size)
        {
            if (string.IsNullOrEmpty(size)) return 100;
            try
            {
                var match = System.Text.RegularExpressions.Regex.Match(size, @"(\d+(?:\.\d+)?)");
                if (match.Success && double.TryParse(match.Value, out double value))
                {
                    if (size.Contains("GB", StringComparison.OrdinalIgnoreCase)) return (int)(value * 1024);
                    return (int)value;
                }
            }
            catch { }
            return 100;
        }
        
        private List<string> GetSelectedApps()
        {
            return appCheckBoxes.Where(kv => kv.Value.IsChecked == true && kv.Value.IsEnabled).Select(kv => kv.Key).ToList();
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
                string disk = selectedInstallDrive.TrimEnd('\\');
                var drive = new DriveInfo(disk);
                if (drive.IsReady)
                {
                    long availableMB = drive.AvailableFreeSpace / 1024 / 1024;
                    if (availableMB >= totalRequired)
                    {
                        txtSpaceStatus.Text = $"💾 Диск {disk} | Требуется: ~{totalRequired} МБ | Доступно: {availableMB} МБ ✅";
                        txtSpaceStatus.Foreground = new SolidColorBrush(Color.FromRgb(0, 255, 0));
                    }
                    else
                    {
                        txtSpaceStatus.Text = $"💾 Диск {disk} | Требуется: ~{totalRequired} МБ | Доступно: {availableMB} МБ ❌ Мало места!";
                        txtSpaceStatus.Foreground = new SolidColorBrush(Color.FromRgb(255, 100, 100));
                    }
                }
            }
            catch (Exception ex) { AddLog($"⚠️ Ошибка проверки места: {ex.Message}"); }
        }
        
        public void AddLog(string message)
        {
            Dispatcher.Invoke(() =>
            {
                txtInstallLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n");
                txtInstallLog.ScrollToEnd();
            });
            LogMessage?.Invoke(message);
        }
    }
}
