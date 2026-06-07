using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
        private static readonly HttpClient _httpClient = CreateHttpClient();

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent", "Ven4Tools");
            return client;
        }

        private readonly string[] wingetSources = { "winget", "msstore" };
        private readonly SemaphoreSlim installSemaphore = new SemaphoreSlim(2);
        private bool isCancelled = false;
        private CancellationTokenSource? cancellationTokenSource;
        private readonly AppManager appManager = null!;
        private readonly InstallationService installService = null!;
        private readonly AvailabilityChecker availabilityChecker = null!;
        private readonly InstalledAppsService installedAppsService = new();
        private readonly UserAppsService _userAppsService = new();
        private readonly FavoritesService _favoritesService = new();
        private Dictionary<string, CheckBox> appCheckBoxes = new();
        private Dictionary<AppCategory, StackPanel> categoryPanels = new();
        private Dictionary<string, AvailabilityChecker.AvailabilityStatus> availabilityStatus = new();
        private Dictionary<string, ComboBox> _versionCombos = new();
        private Dictionary<string, List<string>> _appVersions = new();
        private readonly VersionTrackingService _versionTracker = new();
        private MasterCatalog? _catalog;
        private CatalogLoaderService? _catalogLoader;
        private string selectedInstallDrive = "C:\\";
        private string systemDrive = "C:\\";
        private bool _isCheckingAvailability = false;
        private bool _initialized = false;
        private bool _eventsSubscribed = false;
        private bool _showFavoritesOnly = false;
        private bool _showRuBlocked = true;
        private readonly HashSet<string> _ruBlockedIds = new();
        private CancellationTokenSource? _searchDebounce;
        private Action? _profileChangedHandler;
        private readonly ObservableCollection<LogEntry> _logEntries = new();

        public event Action<string>? LogMessage;
        public event Action? SwitchToUpdatesRequested;
        
        // Categories visible per mode
        private static readonly HashSet<AppCategory> BasicCategories = new()
        {
            AppCategory.Браузеры, AppCategory.Офис, AppCategory.Мультимедиа,
            AppCategory.Системные, AppCategory.Другое
        };
        private static readonly HashSet<AppCategory> ExtendedCategories = new(BasicCategories)
        {
            AppCategory.Разработка, AppCategory.Графика, AppCategory.Мессенджеры
        };

        public CatalogTab()
        {
            InitializeComponent();
            lstInstallLog.ItemsSource = _logEntries;

            _catalogLoader = new CatalogLoaderService();
            appManager = new AppManager();
            installService = new InstallationService();
            availabilityChecker = new AvailabilityChecker();

            InitCategoryPanels();
            LoadAvailableDisks();

            _profileChangedHandler = () => Dispatcher.Invoke(ApplyProfileFilters);

            Loaded += async (s, e) =>
            {
                // Loaded может срабатывать многократно (переключение вкладок) —
                // подписываемся только один раз, иначе обработчики дублируются.
                if (!_eventsSubscribed)
                {
                    ProfileService.Changed += _profileChangedHandler;
                    UserSession.Changed    += OnUserSessionChanged;
                    AppSettings.Changed    += OnAppSettingsChanged;
                    SourceOrderService.Changed += OnSourceOrderChanged;
                    _eventsSubscribed = true;
                }
                if (!_initialized)
                {
                    _initialized = true;
                    await LoadCatalogAndRefreshAsync();
                }
                ApplyCategorySourceHeaders();
            };
            Unloaded += (_, _) =>
            {
                if (_eventsSubscribed)
                {
                    ProfileService.Changed -= _profileChangedHandler;
                    UserSession.Changed    -= OnUserSessionChanged;
                    AppSettings.Changed    -= OnAppSettingsChanged;
                    SourceOrderService.Changed -= OnSourceOrderChanged;
                    _eventsSubscribed = false;
                }
                (installService as IDisposable)?.Dispose();
                (availabilityChecker as IDisposable)?.Dispose();
            };
            
            btnInstall.Click += InstallSelected_Click;
            btnCancelInstall.Click += CancelInstall_Click;
            btnCheckUpdates.Click += BtnCheckUpdates_Click;
            btnClearAllUserApps.Click += BtnClearAllUserApps_Click;
            btnRefreshCatalog.Click += BtnRefreshCatalog_Click;
            btnRefreshAvailability.Click += RefreshAvailability_Click;
            cmbAvailableDisks.SelectionChanged += CmbAvailableDisks_SelectionChanged;
            txtSearch.TextChanged += TxtSearch_TextChanged;
            txtSearch.GotFocus += (_, _) => { if (txtSearch.Text == (string)txtSearch.Tag) txtSearch.Text = ""; };
            txtSearch.LostFocus += (_, _) => { if (string.IsNullOrWhiteSpace(txtSearch.Text)) txtSearch.Text = (string)txtSearch.Tag; };
            txtSearch.Text = (string)txtSearch.Tag;
            btnClearSearch.Click += (_, _) => { txtSearch.Text = (string)txtSearch.Tag; };
            btnFavoritesOnly.Click += BtnFavoritesOnly_Click;
            btnFavoritesOnly.Foreground = new SolidColorBrush(Color.FromRgb(100, 100, 100));

            _showRuBlocked = ProfileService.Current.ShowRuBlocked;
            btnShowRuBlocked.Click += BtnShowRuBlocked_Click;
            UpdateRuBlockedButton();
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
            ShowLoading();
            try
            {
                if (_catalogLoader == null) return;

                // Fast-path: splash already preloaded the catalog
                if (CatalogLoaderService.LoadedCatalog != null)
                {
                    _catalog = CatalogLoaderService.LoadedCatalog;
                    SyncCatalogToAppManager();
                    appManager.LoadAlternativeSources();

                    string preloadedSource = _catalog.Source switch
                    {
                        "online" => "🌐 Каталог загружен из интернета",
                        "cache" => "💾 Каталог из кэша (интернет недоступен)",
                        "embedded" => "📀 Встроенный каталог (минимальный набор)",
                        _ => "❓ Неизвестный источник"
                    };
                    AddLog(preloadedSource);
                    AddLog($"📦 Каталог предзагружен: {_catalog.Apps.Count} приложений");

                    LoadApps();

                    if (UserSession.IsLoggedIn)
                        await SyncUserAppsFromServerAsync();

                    return;
                }

                var loadedCatalog = await _catalogLoader.LoadCatalogAsync();
                
                if (loadedCatalog == null)
                {
                    AddLog("❌ Ошибка: не удалось загрузить каталог");
                    return;
                }
                
                _catalog = loadedCatalog;
                SyncCatalogToAppManager();
                appManager.LoadAlternativeSources();

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

                if (UserSession.IsLoggedIn)
                    await SyncUserAppsFromServerAsync();
            }
            catch (Exception ex)
            {
                AddLog($"Ошибка загрузки каталога: {ex.Message}");
            }
            finally
            {
                HideLoading();
            }
        }

        private void OnUserSessionChanged()
        {
            _ = Dispatcher.InvokeAsync(async () =>
            {
                // Clear previous user's apps from UI and local storage
                var userApps = appManager.GetAllApps().Where(a => a.IsUserAdded).ToList();
                foreach (var app in userApps)
                {
                    appCheckBoxes.Remove(app.Id);
                    availabilityStatus.Remove(app.Id);
                }
                PanelПользовательские.Children.Clear();
                appManager.ClearUserApps();

                if (UserSession.IsLoggedIn)
                    await SyncUserAppsFromServerAsync();
            });
        }

        private async Task SyncUserAppsFromServerAsync()
        {
            if (!UserSession.IsLoggedIn) return;
            var serverApps = await _userAppsService.FetchAsync(UserSession.UserId);
            int added = 0;
            foreach (var app in serverApps)
            {
                if (appManager.GetAppById(app.Id) != null) continue;
                appManager.AddUserApp(app);
                await Dispatcher.InvokeAsync(() => AddUserAppToUI(app));
                added++;
            }
            if (added > 0)
                AddLog($"☁️ Синхронизировано {added} приложений с аккаунта");
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
                _versionCombos.Clear();
                _appVersions.Clear();

                if (_catalog == null)
                {
                    AddLog("⚠️ Каталог не загружен");
                    return;
                }

                AddLog($"Каталог содержит {_catalog.Apps.Count} приложений");

                var availabilityTasks = new List<Task>();

                _ruBlockedIds.Clear();

                var appsToShow = ProfileService.Current.DefaultSort switch
                {
                    "alpha"      => _catalog.Apps.OrderBy(a => a.Name).ToList(),
                    "category"   => _catalog.Apps.OrderBy(a => a.Category).ThenBy(a => a.Name).ToList(),
                    "popularity" => _catalog.Apps.OrderByDescending(a => a.Popularity).ThenBy(a => a.Name).ToList(),
                    _            => _catalog.Apps
                };

                foreach (var app in appsToShow)
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
                            ToolTip = "⏳ Проверка доступности..."
                        };

                        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
                        row.Children.Add(checkBox);
                        row.Children.Add(MakeStarButton(app.Id));
                        if (app.RuBlocked)
                        {
                            _ruBlockedIds.Add(app.Id);
                            row.Children.Add(new TextBlock
                            {
                                Text = "⚠ РФ",
                                Foreground = new SolidColorBrush(Color.FromRgb(255, 165, 0)),
                                FontSize = 10,
                                VerticalAlignment = VerticalAlignment.Center,
                                Margin = new Thickness(4, 0, 0, 0),
                                ToolTip = "Загрузка может быть заблокирована в России"
                            });
                        }
                        if (!string.IsNullOrEmpty(app.WingetId))
                        {
                            var versionCombo = MakeVersionCombo(app.Id);
                            row.Children.Add(versionCombo);
                            _versionCombos[app.Id] = versionCombo;
                        }

                        panel.Children.Add(row);
                        appCheckBoxes[app.Id] = checkBox;
                        availabilityTasks.Add(CheckAppAvailabilityFromCatalog(app));
                    }
                }

                var userApps = appManager.GetAllApps().Where(a => a.IsUserAdded).ToList();
                foreach (var app in userApps)
                {
                    AddUserAppToUI(app);
                }

                AddLog($"Загружено {_catalog.Apps.Count} приложений из каталога, {userApps.Count} пользовательских");
                AddLog("⏳ Проверка доступности запущена...");

                ApplyProfileFilters();

                _ = Task.WhenAll(availabilityTasks).ContinueWith(async _ =>
                {
                    // availabilityStatus заполняется/читается на UI-потоке — агрегируем там,
                    // иначе чтение словаря с пулового потока гонится с записью из Dispatcher.
                    await Dispatcher.InvokeAsync(() =>
                    {
                        int available = availabilityStatus.Values.Count(s => s == AvailabilityChecker.AvailabilityStatus.Available);
                        int unavailable = availabilityStatus.Values.Count(s => s == AvailabilityChecker.AvailabilityStatus.Unavailable);
                        AddLog($"✅ Проверка завершена: {available} доступно, {unavailable} недоступно");
                    });
                    await FetchVersionsPhase2Async();
                    await UpdateInstalledStatusAsync();
                }).Unwrap().ContinueWith(t =>
                {
                    if (t.IsFaulted)
                        AddLog($"⚠️ Ошибка фоновой задачи: {t.Exception?.InnerException?.Message}");
                });
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

            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            panel.Children.Add(checkBox);
            panel.Children.Add(MakeStarButton(app.Id));
            if (!string.IsNullOrEmpty(app.AlternativeId))
            {
                var versionCombo = MakeVersionCombo(app.Id);
                panel.Children.Add(versionCombo);
                _versionCombos[app.Id] = versionCombo;
                _ = Task.Run(() => FetchVersionsForAppAsync(app.Id, app.AlternativeId));
            }
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
                    if (UserSession.IsLoggedIn)
                        _ = _userAppsService.DeleteAsync(UserSession.UserId, appId);

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
                        IsUserAdded = false,
                        ChocoId = catalogApp.ChocoId,
                        ScoopId = catalogApp.ScoopId
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
                // checkBox lives inside a row StackPanel; that row lives in the category StackPanel
                var rowPanel = VisualTreeHelper.GetParent(checkBox) as StackPanel;
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
                                    var token = cancellationTokenSource?.Token ?? CancellationToken.None;
                                    _ = Task.Delay(2000, token).ContinueWith(
                                        t => { if (!t.IsCanceled) return CheckSingleAppAvailability(appId, attempt + 1); return Task.CompletedTask; },
                                        TaskScheduler.Default).Unwrap();
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
            if (_isCheckingAvailability) return;
            _isCheckingAvailability = true;
            btnRefreshAvailability.IsEnabled = false;
            availabilityChecker.ClearCache();
            availabilityStatus.Clear();

            AddLog("🔄 Запущена свежая проверка доступности...");

            // Reset UI indicators first
            if (_catalog != null)
                foreach (var app in _catalog.Apps)
                    if (appCheckBoxes.TryGetValue(app.Id, out var cb) && cb.Content is TextBlock tb)
                    { tb.Foreground = Brushes.Gray; cb.ToolTip = "⏳ Проверка..."; cb.IsEnabled = true; }

            foreach (var app in appManager.GetAllApps().Where(a => a.IsUserAdded))
                if (appCheckBoxes.TryGetValue(app.Id, out var cb) && cb.Content is TextBlock tb)
                { tb.Foreground = Brushes.Gray; cb.ToolTip = "⏳ Проверка..."; cb.IsEnabled = true; }

            // Throttle: max 5 concurrent winget show calls
            var sem = new SemaphoreSlim(5);
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
                        appManager.SaveAlternativeSource(item.AppId, item.SelectedPackage!.Id, null);
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

            // Restore point — ask only for bulk installs (≥ 2 apps)
            if (selectedApps.Count >= 2)
            {
                var rpAnswer = MessageBox.Show(
                    $"Будет установлено {selectedApps.Count} приложений.\n\nСоздать точку восстановления Windows перед установкой?",
                    "Точка восстановления",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Question);

                if (rpAnswer == MessageBoxResult.Cancel) return;

                if (rpAnswer == MessageBoxResult.Yes)
                {
                    AddLog("🛡️ Создаю точку восстановления...");
                    bool rpOk = await CreateRestorePointAsync();
                    AddLog(rpOk ? "✅ Точка восстановления создана" : "⚠️ Точка восстановления не создана (можно продолжать)");
                }
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
                await installSemaphore.WaitAsync();
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
                                tb.Foreground = new SolidColorBrush(Color.FromRgb(136, 136, 136));
                                cb.Opacity = 0.7;
                                cb.IsEnabled = false;
                                cb.ToolTip = selectedVersion != null ? $"✅ Установлено ({selectedVersion})" : "✅ Установлено";
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
                _ = UpdateInstalledStatusAsync();
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
                if (UserSession.IsLoggedIn)
                    _ = _userAppsService.SaveAsync(UserSession.UserId, newApp);

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
        
        private void BtnClearAllUserApps_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show("Вы действительно хотите удалить ВСЕ пользовательские приложения?", "Полная очистка",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    appManager.ClearUserApps();
                    AddLog("✅ Пользовательские приложения очищены");
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
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(AppSettings.CatalogTimeout));
                var response = await _httpClient.GetAsync(url, cts.Token);
                AddLog($"📡 HTTP статус: {response.StatusCode}");
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    AddLog($"📦 Размер JSON: {json.Length} байт");
                    var dateMatch = System.Text.RegularExpressions.Regex.Match(json, @"""lastUpdated"":\s*""([^""]+)""");
                    if (dateMatch.Success) AddLog($"📅 Версия на GitHub: {dateMatch.Groups[1].Value}");
                }
                var catalogCachePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "master.json");
                try { if (File.Exists(catalogCachePath)) File.Delete(catalogCachePath); } catch { }
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
                        IsUserAdded = false,
                        ChocoId = catalogApp.ChocoId,
                        ScoopId = catalogApp.ScoopId
                    };
                    appManager.AddCatalogApp(appInfo);
                }
                else if (!existing.IsUserAdded)
                {
                    if (!string.IsNullOrEmpty(catalogApp.DownloadUrl))
                        existing.InstallerUrls = new List<string> { catalogApp.DownloadUrl };
                    if (!string.IsNullOrEmpty(catalogApp.WingetId))
                        existing.AlternativeId = catalogApp.WingetId;
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
        
        private async Task UpdateInstalledStatusAsync()
        {
            await installedAppsService.RefreshAsync();

            int installedCount = 0;
            int outdatedCount = 0;

            await Dispatcher.InvokeAsync(() =>
            {
                foreach (var kvp in appCheckBoxes)
                {
                    var appInfo = appManager.GetAppById(kvp.Key);
                    string wingetId = !string.IsNullOrEmpty(appInfo?.AlternativeId)
                        ? appInfo!.AlternativeId
                        : kvp.Key;

                    if (installedAppsService.IsInstalled(wingetId) && kvp.Value.Content is TextBlock tb)
                    {
                        string version = installedAppsService.GetInstalledVersion(wingetId);

                        bool hasUpdate = false;
                        if (!string.IsNullOrEmpty(version) && _appVersions.TryGetValue(kvp.Key, out var knownVersions) && knownVersions.Count > 0)
                            hasUpdate = version != knownVersions[0];

                        if (hasUpdate)
                        {
                            tb.Foreground = new SolidColorBrush(Color.FromRgb(255, 165, 0));
                            kvp.Value.ToolTip = $"✓ Установлено ({version}) | 🆙 Доступна новая версия";
                            outdatedCount++;
                        }
                        else
                        {
                            tb.Foreground = new SolidColorBrush(Color.FromRgb(100, 149, 237));
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
                if (kvp.Value.ToolTip?.ToString()?.StartsWith("✓") == true)
                {
                    var row = VisualTreeHelper.GetParent(kvp.Value) as FrameworkElement ?? kvp.Value;
                    row.Visibility = Visibility.Collapsed;
                }
            }
        }

        private void ApplyRuBlockedVisibility()
        {
            foreach (var appId in _ruBlockedIds)
            {
                if (!appCheckBoxes.TryGetValue(appId, out var checkBox)) continue;
                var row = VisualTreeHelper.GetParent(checkBox) as FrameworkElement ?? checkBox;
                row.Visibility = _showRuBlocked ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void UpdateRuBlockedButton()
        {
            btnShowRuBlocked.Foreground = _showRuBlocked
                ? new SolidColorBrush(Color.FromRgb(255, 165, 0))
                : new SolidColorBrush(Color.FromRgb(100, 100, 100));
            btnShowRuBlocked.ToolTip = _showRuBlocked
                ? "Скрыть недоступные в РФ"
                : "Показывать недоступные в РФ";
        }

        private void BtnShowRuBlocked_Click(object sender, RoutedEventArgs e)
        {
            _showRuBlocked = !_showRuBlocked;
            ProfileService.Current.ShowRuBlocked = _showRuBlocked;
            ProfileService.Save();
            UpdateRuBlockedButton();
            string query = txtSearch.Text == (string)txtSearch.Tag ? "" : txtSearch.Text;
            if (string.IsNullOrEmpty(query) && !_showFavoritesOnly)
                ApplyRuBlockedVisibility();
            else
                FilterApps(query);
        }

        private void ApplyProfileFilters()
        {
            _showRuBlocked = ProfileService.Current.ShowRuBlocked;
            UpdateRuBlockedButton();

            var mode = ProfileService.Current.CatalogMode;
            bool compact = ProfileService.Current.CompactMode;

            foreach (var kvp in categoryPanels)
            {
                // Show/hide expander based on catalog mode
                if (kvp.Value.Parent is System.Windows.Controls.Expander expander)
                {
                    bool visible = mode switch
                    {
                        "basic"    => BasicCategories.Contains(kvp.Key),
                        "extended" => ExtendedCategories.Contains(kvp.Key),
                        _          => true
                    };
                    expander.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                }

                // Compact mode: adjust checkbox margins
                var margin = compact ? new Thickness(0, 0, 0, 0) : new Thickness(0, 2, 0, 2);
                foreach (var child in kvp.Value.Children)
                {
                    if (child is System.Windows.FrameworkElement fe)
                        fe.Margin = margin;
                }
            }

            // Re-apply hide installed if needed
            if (ProfileService.Current.HideInstalled)
                ApplyHideInstalled();

            ApplyRuBlockedVisibility();
        }

        private void OnAppSettingsChanged()
        {
            _catalogLoader?.UpdateTimeout(AppSettings.CatalogTimeout);
            availabilityChecker.UpdateTimeout(AppSettings.CheckTimeout);
        }

        // ── Search & Favorites filter ─────────────────────────────────────────
        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            string text = txtSearch.Text;
            bool isPlaceholder = text == (string)txtSearch.Tag;
            string query = isPlaceholder ? "" : text;

            btnClearSearch.Visibility = query.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

            int visible = FilterApps(query);

            _searchDebounce?.Cancel();

            if (query.Length >= 2 && visible == 0)
            {
                pnlWingetSuggestions.Visibility = Visibility.Visible;
                pnlWingetResults.Children.Clear();
                txtWingetStatus.Text = "⏳ Поиск по источникам...";
                txtWingetStatus.Visibility = Visibility.Visible;

                _searchDebounce = new CancellationTokenSource();
                var token = _searchDebounce.Token;
                _ = Task.Delay(600, token).ContinueWith(async t =>
                {
                    if (t.IsCanceled) return;
                    var wingetTask = WingetService.SearchAsync(query, token);
                    var chocoTask  = PackageManagerService.SearchChocoAsync(query, token);
                    var scoopTask  = PackageManagerService.SearchScoopAsync(query, token);
                    await Task.WhenAll(wingetTask, chocoTask, scoopTask);
                    if (!token.IsCancellationRequested)
                        await Dispatcher.InvokeAsync(() =>
                            ShowAllSuggestions(query, wingetTask.Result, chocoTask.Result, scoopTask.Result));
                });
            }
            else
            {
                HideWingetSuggestions();
            }
        }

        private void BtnFavoritesOnly_Click(object sender, RoutedEventArgs e)
        {
            _showFavoritesOnly = !_showFavoritesOnly;
            btnFavoritesOnly.Foreground = _showFavoritesOnly
                ? new SolidColorBrush(Color.FromRgb(255, 215, 0))
                : new SolidColorBrush(Color.FromRgb(100, 100, 100));
            btnFavoritesOnly.ToolTip = _showFavoritesOnly ? "Показать все" : "Только избранные";

            string query = txtSearch.Text == (string)txtSearch.Tag ? "" : txtSearch.Text;
            FilterApps(query);
            HideWingetSuggestions();
        }

        private int FilterApps(string searchText)
        {
            bool hasSearch = !string.IsNullOrWhiteSpace(searchText);
            bool filtered = hasSearch || _showFavoritesOnly;
            string lower = searchText.ToLower();

            if (!filtered)
            {
                foreach (var kvp in categoryPanels)
                    foreach (FrameworkElement child in kvp.Value.Children)
                        child.Visibility = Visibility.Visible;
                ApplyProfileFilters();
                return -1;
            }

            int totalVisible = 0;

            foreach (var kvp in categoryPanels)
            {
                int panelVisible = 0;
                foreach (FrameworkElement child in kvp.Value.Children)
                {
                    string? label = GetChildLabel(child);
                    string? appId = GetChildAppId(child);

                    bool matchSearch = !hasSearch || (label?.ToLower().Contains(lower) == true);
                    bool matchFav = !_showFavoritesOnly || (appId != null && _favoritesService.IsFavorite(appId));
                    bool matchRu = _showRuBlocked || (appId == null || !_ruBlockedIds.Contains(appId));

                    bool visible = matchSearch && matchFav && matchRu;
                    child.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                    if (visible) { panelVisible++; totalVisible++; }
                }

                if (kvp.Value.Parent is Expander expander)
                {
                    expander.Visibility = panelVisible > 0 ? Visibility.Visible : Visibility.Collapsed;
                    if (panelVisible > 0) expander.IsExpanded = true;
                }
            }

            return totalVisible;
        }

        private static string? GetChildLabel(FrameworkElement child)
        {
            if (child is CheckBox cb && cb.Content is TextBlock tb)
                return tb.Text;
            if (child is StackPanel sp)
            {
                var innerCb = sp.Children.OfType<CheckBox>().FirstOrDefault();
                if (innerCb?.Content is TextBlock innerTb)
                    return innerTb.Text;
            }
            return null;
        }

        private static string? GetChildAppId(FrameworkElement child)
        {
            if (child is CheckBox cb)
                return cb.Tag?.ToString();
            if (child is StackPanel sp)
            {
                var innerCb = sp.Children.OfType<CheckBox>().FirstOrDefault();
                return innerCb?.Tag?.ToString();
            }
            return null;
        }

        // ── Multi-source search suggestions ──────────────────────────────────────

        private void ShowAllSuggestions(
            string query,
            List<WingetPackage> winget,
            List<(string Id, string Name, string Version)> choco,
            List<(string Id, string Name)> scoop)
        {
            pnlWingetResults.Children.Clear();
            txtWingetStatus.Visibility = Visibility.Collapsed;

            bool any = winget.Count > 0 || choco.Count > 0 || scoop.Count > 0;
            if (!any)
            {
                txtWingetStatus.Text = $"😕 Ничего не найдено по запросу «{query}» ни в одном источнике";
                txtWingetStatus.Visibility = Visibility.Visible;
                return;
            }

            if (winget.Count > 0)
            {
                AddSectionHeader("📦 Winget");
                foreach (var pkg in winget)
                {
                    var captureId = pkg.Id; var captureName = pkg.Name;
                    AddSuggestionRow(pkg.Name, $"winget:{pkg.Id}", pkg.Id,
                        () => AddWingetSuggestion(captureName, captureId));
                }
            }

            if (choco.Count > 0)
            {
                AddSectionHeader("🍫 Chocolatey");
                foreach (var (id, name, ver) in choco)
                {
                    var captureId = id;
                    AddSuggestionRow(name.Length > 0 ? name : id, $"v{ver}", id,
                        () => AddChocoSuggestion(captureId));
                }
            }

            if (scoop.Count > 0)
            {
                AddSectionHeader("🪣 Scoop");
                foreach (var (id, name) in scoop)
                {
                    var captureId = id;
                    AddSuggestionRow(id, "", id,
                        () => AddScoopSuggestion(captureId));
                }
            }
        }

        private void AddSectionHeader(string title)
        {
            var border = new Border
            {
                Margin          = new Thickness(0, 6, 0, 2),
                BorderBrush     = (Brush)FindResource("BorderBrush"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding         = new Thickness(0, 0, 0, 2)
            };
            border.Child = new TextBlock
            {
                Text       = title,
                FontSize   = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("TextSecondary")
            };
            pnlWingetResults.Children.Add(border);
        }

        private void AddSuggestionRow(string name, string hint, string tooltip, Action onClick)
        {
            var btn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 1, 0, 1), Height = 26,
                Padding = new Thickness(6, 0, 6, 0),
                Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand, FontSize = 12, ToolTip = tooltip
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock { Text = "➕  ", Foreground = (Brush)FindResource("AccentColor"), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = name, Foreground = (Brush)FindResource("TextPrimary"), VerticalAlignment = VerticalAlignment.Center });
            if (!string.IsNullOrEmpty(hint))
                row.Children.Add(new TextBlock { Text = $"  {hint}", Foreground = (Brush)FindResource("TextSecondary"), FontSize = 10, VerticalAlignment = VerticalAlignment.Center });
            btn.Content = row;
            btn.Click += (_, _) => onClick();
            pnlWingetResults.Children.Add(btn);
        }

        private void AddChocoSuggestion(string id)
        {
            if (appManager.GetAppById(id) != null) { AddLog($"ℹ️ {id} уже есть в списке"); return; }
            var app = new AppInfo { Id = id, DisplayName = id, Category = AppCategory.Другое, ChocoId = id, InstallerUrls = new List<string>(), IsUserAdded = true };
            appManager.AddUserApp(app);
            AddUserAppToUI(app);
            AddLog($"➕ Добавлено из Chocolatey: {id}");
        }

        private void AddScoopSuggestion(string id)
        {
            if (appManager.GetAppById(id) != null) { AddLog($"ℹ️ {id} уже есть в списке"); return; }
            var app = new AppInfo { Id = id, DisplayName = id, Category = AppCategory.Другое, ScoopId = id, InstallerUrls = new List<string>(), IsUserAdded = true };
            appManager.AddUserApp(app);
            AddUserAppToUI(app);
            AddLog($"➕ Добавлено из Scoop: {id}");
        }

private void HideWingetSuggestions()
        {
            Dispatcher.Invoke(() =>
            {
                pnlWingetSuggestions.Visibility = Visibility.Collapsed;
                pnlWingetResults.Children.Clear();
            });
        }

        private async void AddWingetSuggestion(string name, string id)
        {
            try
            {
                if (!UserSession.IsLoggedIn)
                {
                    var prompt = MessageBox.Show(
                        $"Добавить «{name}» в локальный список?\n\nДля синхронизации между устройствами войдите в аккаунт.",
                        "Добавить приложение", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (prompt != MessageBoxResult.Yes) return;
                }

                if (appManager.GetAppById(id) != null)
                {
                    AddLog($"ℹ️ {name} уже есть в списке");
                    return;
                }

                var newApp = new AppInfo
                {
                    Id = id,
                    DisplayName = name,
                    Category = AppCategory.Другое,
                    AlternativeId = id,
                    InstallerUrls = new List<string>(),
                    SilentArgs = "",
                    IsUserAdded = true
                };

                appManager.AddUserApp(newApp);
                AddUserAppToUI(newApp);

                if (UserSession.IsLoggedIn)
                    await _userAppsService.SaveAsync(UserSession.UserId, newApp);

                AddLog($"➕ Добавлено из winget: {name} ({id})");

                txtSearch.Text = (string)txtSearch.Tag;
                HideWingetSuggestions();
            }
            catch (Exception ex)
            {
                AddLog($"❌ Ошибка добавления приложения: {ex.Message}");
            }
        }

        // ── Loading indicator ─────────────────────────────────────────────────
        private void ShowLoading() =>
            Dispatcher.Invoke(() => txtLoadingStatus.Visibility = Visibility.Visible);

        private void HideLoading() =>
            Dispatcher.Invoke(() => txtLoadingStatus.Visibility = Visibility.Collapsed);

        // ── Favorites ─────────────────────────────────────────────────────────
        private Button MakeStarButton(string appId)
        {
            bool fav = _favoritesService.IsFavorite(appId);
            var btn = new Button
            {
                Content = fav ? "★" : "☆",
                Width = 20,
                Height = 20,
                Margin = new Thickness(4, 0, 0, 0),
                Tag = appId,
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = fav
                    ? new SolidColorBrush(Color.FromRgb(255, 215, 0))
                    : new SolidColorBrush(Color.FromRgb(100, 100, 100)),
                FontSize = 14,
                Padding = new Thickness(0),
                VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = fav ? "Убрать из избранного" : "Добавить в избранное",
                Cursor = Cursors.Hand
            };
            btn.Click += StarButton_Click;
            return btn;
        }

        private void StarButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not string appId) return;
            _favoritesService.Toggle(appId);
            bool fav = _favoritesService.IsFavorite(appId);
            btn.Content = fav ? "★" : "☆";
            btn.Foreground = fav
                ? new SolidColorBrush(Color.FromRgb(255, 215, 0))
                : new SolidColorBrush(Color.FromRgb(100, 100, 100));
            btn.ToolTip = fav ? "Убрать из избранного" : "Добавить в избранное";
        }

        // ── Version selection combo ───────────────────────────────────────────
        private ComboBox MakeVersionCombo(string appId)
        {
            var combo = new ComboBox
            {
                Width = 95,
                Height = 22,
                Margin = new Thickness(4, 0, 0, 0),
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
                IsEnabled = false,
                ToolTip = "Версии загружаются...",
                Tag = appId
            };
            combo.Items.Add("Последняя");
            combo.SelectedIndex = 0;
            return combo;
        }

        private async Task FetchVersionsForAppAsync(string appId, string wingetId)
        {
            var versions = await WingetVersionsService.FetchVersionsAsync(wingetId);
            if (versions.Count == 0) return;
            await Dispatcher.InvokeAsync(() =>
            {
                // Запись в _appVersions выполняется на UI-потоке: Phase2 запускает
                // несколько FetchVersionsForAppAsync параллельно, а Dictionary не
                // потокобезопасен — одновременная запись могла повредить структуру.
                _appVersions[appId] = versions;
                if (_versionCombos.TryGetValue(appId, out var combo))
                {
                    combo.Items.Clear();
                    combo.Items.Add("Последняя");
                    foreach (var v in versions)
                        combo.Items.Add(v);
                    combo.SelectedIndex = 0;
                    combo.IsEnabled = true;
                    combo.ToolTip = $"Доступных версий: {versions.Count}";
                }
            });
        }

        private async Task FetchVersionsPhase2Async()
        {
            if (_catalog == null) return;
            AddLog("🔍 Загрузка доступных версий (Фаза 2)...");

            var sem = new SemaphoreSlim(3);
            var tasks = new List<Task>();

            foreach (var app in _catalog.Apps)
            {
                if (string.IsNullOrEmpty(app.WingetId)) continue;
                if (!_versionCombos.ContainsKey(app.Id)) continue;
                if (availabilityStatus.TryGetValue(app.Id, out var s) &&
                    s == AvailabilityChecker.AvailabilityStatus.Unavailable) continue;

                var localApp = app;
                tasks.Add(Task.Run(async () =>
                {
                    await sem.WaitAsync();
                    try { await FetchVersionsForAppAsync(localApp.Id, localApp.WingetId); }
                    finally { sem.Release(); }
                }));
            }

            await Task.WhenAll(tasks);
            int loaded = _appVersions.Count;
            AddLog($"✅ Версии загружены для {loaded} приложений");
            _ = UpdateInstalledStatusAsync();
        }

        // ── Update check ──────────────────────────────────────────────────────
        private void BtnCheckUpdates_Click(object sender, RoutedEventArgs e)
        {
            AddLog("🔔 Переход к управлению обновлениями...");
            SwitchToUpdatesRequested?.Invoke();
        }

        public void AddLog(string message)
        {
            Dispatcher.Invoke(() =>
            {
                var entry = LogEntry.Parse(message);
                _logEntries.Add(entry);
                lstInstallLog.ScrollIntoView(entry);
            });
            LogMessage?.Invoke(message);
        }

        private void LogList_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
            {
                CopyLog_Click(sender, e);
                e.Handled = true;
            }
        }

        private void CopyLog_Click(object sender, RoutedEventArgs e)
        {
            var items = lstInstallLog.SelectedItems.Count > 0
                ? lstInstallLog.SelectedItems.Cast<LogEntry>()
                : _logEntries.AsEnumerable();
            var text = string.Join(Environment.NewLine,
                items.Select(entry => $"[{entry.Time}] {entry.Icon} {entry.Message}"));
            if (!string.IsNullOrEmpty(text))
                Clipboard.SetText(text);
        }

        private void ClearLog_Click(object sender, RoutedEventArgs e) => _logEntries.Clear();

        // ── Local installer (drag & drop) ────────────────────────────────────────

        public void AddLocalInstallerApp(AppInfo app)
        {
            appManager.AddUserApp(app);
            AddUserAppToUI(app);
            AddLog($"📦 Локальный установщик добавлен: {app.DisplayName}");
        }

        // ── Per-category source headers ───────────────────────────────────────────

        private static readonly Dictionary<AppCategory, string> CategoryNames = new()
        {
            [AppCategory.Браузеры]         = "Браузеры",
            [AppCategory.Офис]             = "Офис",
            [AppCategory.Графика]          = "Графика",
            [AppCategory.Разработка]       = "Разработка",
            [AppCategory.Мессенджеры]      = "Мессенджеры",
            [AppCategory.Мультимедиа]      = "Мультимедиа",
            [AppCategory.Системные]        = "Системные",
            [AppCategory.ИгровыеСервисы]   = "Игровые сервисы",
            [AppCategory.Драйверпаки]      = "Драйверпаки",
            [AppCategory.Другое]           = "Другое",
            [AppCategory.Пользовательские] = "Пользовательские",
        };

        private void ApplyCategorySourceHeaders()
        {
            bool perCategory = SourceOrderService.Current.Mode == "per_category";

            foreach (var (category, panel) in categoryPanels)
            {
                if (panel.Parent is not Expander expander) continue;
                if (!CategoryNames.TryGetValue(category, out var catName)) continue;

                if (!perCategory)
                {
                    // Restore plain string header (original icon + name)
                    expander.Header = GetOriginalExpanderHeader(category);
                    continue;
                }

                // Build compound header: [Icon + Name] .......... [Source ComboBox]
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var label = new TextBlock
                {
                    Text = GetOriginalExpanderHeader(category),
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (System.Windows.Media.Brush)Application.Current.Resources["TextPrimary"],
                    FontWeight = FontWeights.SemiBold,
                    FontSize   = 13
                };
                Grid.SetColumn(label, 0);

                var combo = new ComboBox
                {
                    Width   = 160,
                    Height  = 24,
                    FontSize = 11,
                    Margin  = new Thickness(8, 0, 4, 0),
                    Tag     = catName
                };

                combo.Items.Add(new ComboBoxItem { Content = "🔀 Глобальный", Tag = "" });
                foreach (var srcId in SourceOrderSettings.AllSources)
                    combo.Items.Add(new ComboBoxItem { Content = SourceOrderSettings.Labels[srcId], Tag = srcId });

                string current = SourceOrderService.GetCategoryPrimary(catName);
                combo.SelectedIndex = string.IsNullOrEmpty(current)
                    ? 0
                    : SourceOrderSettings.AllSources.IndexOf(current) + 1;

                combo.SelectionChanged += (s, _) =>
                {
                    if (combo.SelectedItem is ComboBoxItem item)
                    {
                        string src = item.Tag?.ToString() ?? "";
                        SourceOrderService.SetCategoryPrimary(catName, src);
                    }
                };

                Grid.SetColumn(combo, 1);
                grid.Children.Add(label);
                grid.Children.Add(combo);
                expander.Header = grid;
            }
        }

        private static string GetOriginalExpanderHeader(AppCategory cat) => cat switch
        {
            AppCategory.Браузеры         => "🌐 Браузеры",
            AppCategory.Офис             => "📁 Офис",
            AppCategory.Графика          => "🎨 Графика",
            AppCategory.Разработка       => "💻 Разработка",
            AppCategory.Мессенджеры      => "💬 Мессенджеры",
            AppCategory.Мультимедиа      => "🎵 Мультимедиа",
            AppCategory.Системные        => "⚙️ Системные",
            AppCategory.ИгровыеСервисы   => "🎮 Игровые сервисы",
            AppCategory.Драйверпаки      => "🖨️ Драйверпаки",
            AppCategory.Другое           => "📎 Другое",
            AppCategory.Пользовательские => "👤 Пользовательские",
            _                            => cat.ToString()
        };

        private void OnSourceOrderChanged()
        {
            Dispatcher.Invoke(() =>
            {
                ApplyCategorySourceHeaders();
                RefreshAvailability_Click(this, new RoutedEventArgs());
            });
        }

        // ── Export / Import app list ──────────────────────────────────────────────

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
                File.WriteAllText(dlg.FileName, json, System.Text.Encoding.UTF8);
                AddLog($"📤 Экспорт: {selected.Count} приложений → {Path.GetFileName(dlg.FileName)}");
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
                string json = File.ReadAllText(dlg.FileName, System.Text.Encoding.UTF8);
                var doc = Newtonsoft.Json.Linq.JObject.Parse(json);
                var ids = doc["app_ids"]?.ToObject<List<string>>()
                       ?? doc["apps"]?.ToObject<List<string>>()
                       ?? new List<string>();

                int matched = 0, skipped = 0;
                foreach (var id in ids)
                {
                    if (appCheckBoxes.TryGetValue(id, out var cb))
                    {
                        cb.IsChecked = true;
                        matched++;
                    }
                    else
                    {
                        skipped++;
                    }
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

        // ── Restore point ─────────────────────────────────────────────────────────

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

    public sealed class LogEntry
    {
        private static readonly SolidColorBrush BrushGreen   = Frozen(0x4C, 0xAF, 0x50);
        private static readonly SolidColorBrush BrushRed     = Frozen(0xF4, 0x43, 0x36);
        private static readonly SolidColorBrush BrushOrange  = Frozen(0xFF, 0x98, 0x00);
        private static readonly SolidColorBrush BrushTeal    = Frozen(0x00, 0xC3, 0xAA);
        private static readonly SolidColorBrush BrushMuted   = Frozen(0x6B, 0x8C, 0xAE);
        private static readonly SolidColorBrush BrushPurple  = Frozen(0x9C, 0x27, 0xB0);

        public string Time        { get; }
        public string Icon        { get; }
        public string Message     { get; }
        public SolidColorBrush IconBrush   { get; }
        public SolidColorBrush AccentBrush { get; }

        private LogEntry(string time, string icon, string message,
                         SolidColorBrush iconBrush, SolidColorBrush accentBrush)
        {
            Time = time; Icon = icon; Message = message;
            IconBrush = iconBrush; AccentBrush = accentBrush;
        }

        public static LogEntry Parse(string raw)
        {
            string time = DateTime.Now.ToString("HH:mm:ss");
            string text = raw.TrimStart();

            (string icon, string msg, SolidColorBrush iconBrush, SolidColorBrush accent) =
                text switch
                {
                    _ when text.StartsWith("✅") => ("✅", text[2..].TrimStart(), BrushGreen,  BrushGreen),
                    _ when text.StartsWith("❌") => ("❌", text[2..].TrimStart(), BrushRed,    BrushRed),
                    _ when text.StartsWith("⚠️") => ("⚠️", text[3..].TrimStart(), BrushOrange, BrushOrange),
                    _ when text.StartsWith("➕") => ("➕", text[2..].TrimStart(), BrushOrange, BrushOrange),
                    _ when text.StartsWith("🗑️") => ("🗑️", text[3..].TrimStart(), BrushOrange, BrushOrange),
                    _ when text.StartsWith("📦") => ("📦", text[2..].TrimStart(), BrushTeal,   BrushTeal),
                    _ when text.StartsWith("📡") => ("📡", text[2..].TrimStart(), BrushTeal,   BrushTeal),
                    _ when text.StartsWith("💾") => ("💾", text[2..].TrimStart(), BrushTeal,   BrushTeal),
                    _ when text.StartsWith("📅") => ("📅", text[2..].TrimStart(), BrushTeal,   BrushTeal),
                    _ when text.StartsWith("📋") => ("📋", text[2..].TrimStart(), BrushTeal,   BrushTeal),
                    _ when text.StartsWith("📥") => ("📥", text[2..].TrimStart(), BrushTeal,   BrushTeal),
                    _ when text.StartsWith("📤") => ("📤", text[2..].TrimStart(), BrushTeal,   BrushTeal),
                    _ when text.StartsWith("🔍") => ("🔍", text[2..].TrimStart(), BrushTeal,   BrushTeal),
                    _ when text.StartsWith("ℹ️") => ("ℹ️", text[3..].TrimStart(), BrushTeal,   BrushTeal),
                    _ when text.StartsWith("☁️") => ("☁️", text[3..].TrimStart(), BrushTeal,   BrushTeal),
                    _ when text.StartsWith("🔄") => ("🔄", text[2..].TrimStart(), BrushMuted,  BrushMuted),
                    _ when text.StartsWith("⏳") => ("⏳", text[2..].TrimStart(), BrushMuted,  BrushMuted),
                    _ when text.StartsWith("🔔") => ("🔔", text[2..].TrimStart(), BrushMuted,  BrushMuted),
                    _ when text.StartsWith("🆙") => ("🆙", text[2..].TrimStart(), BrushGreen,  BrushGreen),
                    _ when text.StartsWith("🛡️") => ("🛡️", text[3..].TrimStart(), BrushPurple, BrushPurple),
                    _ when text.StartsWith("🔑") => ("🔑", text[2..].TrimStart(), BrushPurple, BrushPurple),
                    _                             => ("·",  text,                  BrushMuted,  BrushMuted),
                };

            return new LogEntry(time, icon, msg, iconBrush, accent);
        }

        private static SolidColorBrush Frozen(byte r, byte g, byte b)
        {
            var br = new SolidColorBrush(Color.FromRgb(r, g, b));
            br.Freeze();
            return br;
        }
    }
}
