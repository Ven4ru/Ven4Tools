using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;  // ← ДОБАВЬТЕ ЭТУ СТРОКУ
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using Microsoft.Win32;
using Ven4Tools.Models;
using Ven4Tools.Services;

namespace Ven4Tools
{
    public partial class MainWindow : Window
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
        private const string TurboBoostRegPath = @"SYSTEM\ControlSet001\Control\Power\PowerSettings\54533251-82be-4824-96c1-47b60b740d00\be337238-0d82-4146-a960-4f3749d470c7";
               
        // Для выбора диска
        private string selectedInstallDrive = "C:\\";
        private string systemDrive = "C:\\";


// Добавляем проверку при старте (опционально)


public MainWindow()
{
    if (!IsRunAsAdmin())
    {
        RestartAsAdmin();
        return;
    }

    InitializeComponent();
    _catalogLoader = new CatalogLoaderService();
    Loaded += async (s, e) => await LoadCatalogAndRefreshAsync();
    appManager = new AppManager();
    installService = new InstallationService();
    availabilityChecker = new AvailabilityChecker();
    txtAdminStatus.Text = "✅ Активированы права администратора";
    
    // Устанавливаем версию
    string currentVersion = "2.3.0";
    txtVersionTitle.Text = $"Ven4Tools v{currentVersion}";
    
    InitCategoryPanels();
    LoadAvailableDisks();
    
    // LoadApps будет вызван после загрузки каталога
    UpdateSpaceStatus();
    
    try
    {
        File.AppendAllText(@"C:\Users\Ven4\debug_constructor.log", 
            $"{DateTime.Now}: Конструктор выполнен\n");
    }
    catch { }
}

        public void LogMessage(string message)
        {
            Dispatcher.Invoke(() =>
            {
                txtInstallLog.AppendText($"{DateTime.Now:HH:mm:ss} - {message}\n");
                txtInstallLog.ScrollToEnd();
            });
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
                LogMessage($"⚠️ Ошибка получения списка дисков: {ex.Message}");
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
                LogMessage($"⚠️ Ошибка обновления информации о диске: {ex.Message}");
            }
        }

private async void LoadApps()
{
    try
    {
        // Очищаем старые панели
        foreach (var panel in categoryPanels.Values)
        {
            panel.Children.Clear();
        }
        appCheckBoxes.Clear();
        availabilityStatus.Clear();
        
        AddLog($"Каталог содержит {_catalog?.Apps.Count ?? 0} приложений");
        
        // Загружаем приложения из каталога
        if (_catalog != null && _catalog.Apps.Any())
        {
            AddLog($"Загружаем приложения из каталога ({(_catalog?.Apps.Count ?? 0)} шт.)");
            
            foreach (var app in _catalog?.Apps ?? Enumerable.Empty<Models.App>())
            {
                var category = GetCategoryFromString(app.Category);
                
                if (categoryPanels.TryGetValue(category, out var panel) && panel != null)
                {
                    var checkBox = new CheckBox
                    {
                        Content = app.Name,
                        Tag = app.Id,
                        Foreground = Brushes.Gray,
                        Margin = new Thickness(0, 2, 0, 2),
                        ToolTip = "Проверка доступности..."
                    };

                    panel.Children.Add(checkBox);
                    appCheckBoxes[app.Id] = checkBox;
                    _ = CheckAppAvailabilityFromCatalog(app);
                }
                else
                {
                    AddLog($"Категория '{app.Category}' не найдена, добавляем в 'Другое'");
                    if (categoryPanels.TryGetValue(AppCategory.Другое, out var otherPanel))
                    {
                        var checkBox = new CheckBox
                        {
                            Content = app.Name,
                            Tag = app.Id,
                            Foreground = Brushes.Gray,
                            Margin = new Thickness(0, 2, 0, 2),
                            ToolTip = "Проверка доступности..."
                        };
                        otherPanel.Children.Add(checkBox);
                        appCheckBoxes[app.Id] = checkBox;
                        _ = CheckAppAvailabilityFromCatalog(app);
                    }
                }
            }
            
            LogMessage($"Загружено {(_catalog?.Apps.Count ?? 0)} приложений из каталога");
        }
        else
        {
            AddLog("Каталог пуст или не загружен");
        }
        
        // Загружаем пользовательские приложения
        var userApps = appManager.GetAllApps().Where(a => a.IsUserAdded).ToList();
        if (userApps.Any())
        {
            AddLog($"Загружаем пользовательские приложения ({userApps.Count} шт.)");
            
            foreach (var app in userApps)
            {
                AddUserAppToUI(app);
            }
            
            LogMessage($"Загружено {userApps.Count} пользовательских приложений");
        }
    }
    catch (Exception ex)
    {
        LogMessage($"Ошибка при загрузке приложений: {ex.Message}");
        AddLog($"Ошибка: {ex.Message}");
    }
}

// Добавь этот метод в класс MainWindow
private void AddUserAppToUI(AppInfo app)
{
    var checkBox = new CheckBox
    {
        Content = app.DisplayName,
        Tag = app.Id,
        Foreground = Brushes.Orange,
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
}


private async Task LoadCatalogAndRefreshAsync()
{
    try
    {
        if (_catalogLoader == null) return;
        
        _catalog = await _catalogLoader.LoadCatalogAsync();
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
        
        // ВЫЗЫВАЕМ ЗАГРУЗКУ ПРИЛОЖЕНИЙ ПОСЛЕ КАТАЛОГА
        LoadApps();
    }
    catch (Exception ex)
    {
        AddLog($"Ошибка загрузки каталога: {ex.Message}");
    }
}

private async Task CheckAppAvailabilityFromCatalog(Models.App catalogApp)
{
    try
    {
        AddLog($"🔍 Проверка доступности: {catalogApp.Name}");

        // Пытаемся взять AppInfo из AppManager (там уже применены альтернативы)
        AppInfo appInfo = appManager.GetAppById(catalogApp.Id);

        if (appInfo == null)
        {
            AddLog($"   → Приложение {catalogApp.Name} не найдено в AppManager, создаём...");
            
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
        else
        {
            AddLog($"   → Найдено в AppManager, AlternativeId = {appInfo.AlternativeId ?? "нет"}");
        }

        // Логируем, что реально используется
        AddLog($"   → Winget ID для проверки: {appInfo.AlternativeId ?? appInfo.Id}");
        AddLog($"   → Ссылки: {(appInfo.InstallerUrls.Any() ? string.Join(", ", appInfo.InstallerUrls) : "нет")}");

        var availabilityResult = await availabilityChecker.CheckAppAvailabilityWithSize(appInfo);
        var status = availabilityResult.Status;
        var size = availabilityResult.SizeMB;

        await Dispatcher.InvokeAsync(() =>
        {
            if (appCheckBoxes.TryGetValue(catalogApp.Id, out var checkBox))
            {
                availabilityStatus[catalogApp.Id] = status;

                switch (status)
                {
                    case AvailabilityChecker.AvailabilityStatus.Available:
                        checkBox.Foreground = Brushes.LightGreen;
                        checkBox.ToolTip = $"✅ Доступно для установки ({(size > 0 ? $"~{size} МБ" : "размер неизвестен")})";
                        checkBox.IsEnabled = true;
                        LogMessage($"✅ {catalogApp.Name} доступен" + (size > 0 ? $" (~{size} МБ)" : ""));
                        break;

                    case AvailabilityChecker.AvailabilityStatus.Unavailable:
                        checkBox.Foreground = Brushes.LightCoral;
                        checkBox.ToolTip = "❌ Недоступно";
                        checkBox.IsEnabled = false;
                        AddSuggestionButtonFromCatalog(catalogApp, checkBox);
                        LogMessage($"❌ {catalogApp.Name} недоступен");
                        break;

                    default:
                        checkBox.Foreground = Brushes.Gray;
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
        LogMessage($"❌ Ошибка при проверке {catalogApp.Name}: {ex.Message}");
    }
}
private void BtnFeedback_Click(object sender, RoutedEventArgs e)
{
    try
    {
        // Собираем информацию о системе
        var osVersion = Environment.OSVersion.VersionString;
        var appVersion = "2.3.0";
        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ven4Tools", "logs", $"install_{DateTime.Now:yyyy-MM-dd}.log");
        
        var logExists = File.Exists(logPath) ? "✅ Лог есть" : "❌ Лога нет";
        
        // Формируем тело issue
        var title = Uri.EscapeDataString($"Обратная связь: {appVersion}");
        var body = Uri.EscapeDataString(
            $"## Версия\n{appVersion}\n\n" +
            $"## ОС\n{osVersion}\n\n" +
            $"## Описание проблемы\n\n" +
            $"### Что случилось?\n\n" +
            $"### Как воспроизвести?\n\n" +
            $"### Ожидаемое поведение\n\n" +
            $"{logExists}\n\n" +
            $"### Лог\n```\n{GetLastLogLines()}\n```");
        
        var url = $"https://github.com/Ven4ru/Ven4Tools/issues/new?title={title}&body={body}";
        
        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });
        
        AddLog("📧 Открыта форма обратной связи");
    }
    catch (Exception ex)
    {
        AddLog($"❌ Ошибка открытия обратной связи: {ex.Message}");
        MessageBox.Show("Не удалось открыть форму обратной связи.\n" +
                        "Пожалуйста, напишите на GitHub вручную:\n" +
                        "https://github.com/Ven4ru/Ven4Tools/issues",
                        "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}

private string GetLastLogLines(int lines = 50)
{
    try
    {
        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ven4Tools", "logs", $"install_{DateTime.Now:yyyy-MM-dd}.log");
        
        if (!File.Exists(logPath)) return "Лог не найден";
        
        var allLines = File.ReadAllLines(logPath);
        var lastLines = allLines.Skip(Math.Max(0, allLines.Length - lines)).ToArray();
        return string.Join("\n", lastLines);
    }
    catch
    {
        return "Не удалось прочитать лог";
    }
}
private async void BtnRefreshCatalog_Click(object sender, RoutedEventArgs e)
{
    AddLog("🔄 Обновление каталога с GitHub...");
    
    try
    {
        // 1. Показываем текущий URL
        var url = "https://raw.githubusercontent.com/Ven4ru/Ven4Tools/main/Catalog/master.json";
        AddLog($"📡 URL: {url}");
        
        // 2. Проверяем версию на GitHub напрямую
        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(5);
        client.DefaultRequestHeaders.Add("User-Agent", "Ven4Tools");
        
        var response = await client.GetAsync(url);
        AddLog($"📡 HTTP статус: {response.StatusCode}");
        
        if (response.IsSuccessStatusCode)
        {
            var json = await response.Content.ReadAsStringAsync();
            AddLog($"📦 Размер JSON: {json.Length} байт");
            
            // Ищем дату обновления
            var dateMatch = System.Text.RegularExpressions.Regex.Match(json, @"""lastUpdated"":\s*""([^""]+)""");
            if (dateMatch.Success)
            {
                AddLog($"📅 Версия на GitHub: {dateMatch.Groups[1].Value}");
            }
        }
        
        // 3. Очищаем кэш
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
var catalogCachePath = Path.Combine(localAppData, "Ven4Tools", "catalog_cache.json");

AddLog($"🗑️ Путь к кэшу: {catalogCachePath}");

if (File.Exists(catalogCachePath))
{
    AddLog($"🗑️ Файл существует, размер: {new FileInfo(catalogCachePath).Length} байт");
    File.Delete(catalogCachePath);
    AddLog("🗑️ Кэш удалён");
    
    // Проверяем, что удалилось
    if (File.Exists(catalogCachePath))
    {
        AddLog("❌ Ошибка: кэш не удалился!");
    }
    else
    {
        AddLog("✅ Кэш успешно удалён");
    }
}
else
{
    AddLog("⚠️ Кэш не найден");
}
        
        // 4. Загружаем свежий каталог
        AddLog("📥 Загружаем свежий каталог...");
        _catalog = await _catalogLoader.LoadCatalogAsync();
        
        AddLog($"📦 Загружено приложений: {(_catalog?.Apps.Count ?? 0)}");
        AddLog($"📅 Версия в загруженном каталоге: {_catalog?.LastUpdated ?? "неизвестно"}");

        // === ПРИМЕНЯЕМ СОХРАНЁННЫЕ АЛЬТЕРНАТИВЫ ===
        appManager.LoadAlternativeSources();
        appManager.ApplyAlternativesToCatalog(_catalog);
AddLog("🔄 Сохранённые альтернативы применены к каталогу");
        // ===========================================

        // 5. Перезагружаем интерфейс
        AddLog("🔄 Перезагружаем интерфейс...");
        LoadApps();
        
        AddLog("✅ Каталог успешно обновлён");
    }
    catch (Exception ex)
    {
        AddLog($"❌ Ошибка: {ex.Message}");
        AddLog($"📋 StackTrace: {ex.StackTrace}");
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
            if (size.Contains("GB", StringComparison.OrdinalIgnoreCase))
                return (int)(value * 1024);
            return (int)value;
        }
    }
    catch { }
    return 100;
}

private void AddSuggestionButtonFromCatalog(Models.App catalogApp, CheckBox checkBox)
{
    var parentPanel = VisualTreeHelper.GetParent(checkBox) as StackPanel;
    if (parentPanel == null) return;

    var existingButton = parentPanel.Children
        .OfType<Button>()
        .FirstOrDefault(b => b.Tag?.ToString() == catalogApp.Id + "_suggest");
    
    if (existingButton != null) return;

    var newRow = new StackPanel 
    { 
        Orientation = Orientation.Horizontal,
        Margin = new Thickness(0, 2, 0, 2)
    };

    var newCheckBox = new CheckBox
    {
        Content = checkBox.Content,
        Tag = checkBox.Tag,
        Foreground = checkBox.Foreground,
        Margin = new Thickness(0, 0, 5, 0),
        IsChecked = checkBox.IsChecked,
        ToolTip = checkBox.ToolTip,
        IsEnabled = checkBox.IsEnabled
    };

    var suggestButton = new Button
    {
        Content = "🔄",
        Width = 20,
        Height = 20,
        Margin = new Thickness(0, 0, 0, 0),
        Tag = catalogApp.Id + "_suggest",
        Background = new SolidColorBrush(Color.FromRgb(255, 140, 0)),
        Foreground = Brushes.White,
        FontSize = 10,
        Padding = new Thickness(0),
        ToolTip = "Предложить альтернативный источник",
        Cursor = Cursors.Hand,
        VerticalAlignment = VerticalAlignment.Center
    };
    
    suggestButton.Click += async (s, e) => await SuggestAlternativeForCatalog(catalogApp);

    newRow.Children.Add(newCheckBox);
    newRow.Children.Add(suggestButton);

    int index = parentPanel.Children.IndexOf(checkBox);
    parentPanel.Children.RemoveAt(index);
    parentPanel.Children.Insert(index, newRow);

    appCheckBoxes[catalogApp.Id] = newCheckBox;
}

private async Task SuggestAlternativeForCatalog(Models.App catalogApp)
{
    try
    {
        AddLog($"🔍 Поиск альтернативы для: {catalogApp.Name}");

        var dialog = new AlternativeSourceDialog(catalogApp.Name) { Owner = this };

        if (dialog.ShowDialog() == true)
        {
            bool saved = false;

            if (dialog.SelectedPackage != null)
            {
                AddLog($"   → Сохраняем Winget ID: {dialog.SelectedPackage.Id}");

                appManager.SaveAlternativeSource(
                    catalogApp.Id,
                    dialog.SelectedPackage.Id,
                    null,
                    dialog.UseWingetFirst);

                saved = true;
            }
            else if (!string.IsNullOrEmpty(dialog.CustomUrl))
            {
                AddLog($"   → Сохраняем ссылку: {dialog.CustomUrl}");

                appManager.SaveAlternativeSource(
                    catalogApp.Id,
                    null,
                    dialog.CustomUrl,
                    dialog.UseUrlFirst);

                saved = true;
            }

            if (saved)
            {
                AddLog($"✅ Альтернатива сохранена для {catalogApp.Name}");

                // Принудительно обновляем статус
                await Task.Delay(500);
                await CheckAppAvailabilityFromCatalog(catalogApp);
            }
        }
    }
    catch (Exception ex)
    {
        AddLog($"❌ Ошибка при сохранении альтернативы: {ex.Message}");
    }
}

private async Task CheckSingleAppAvailability(string appId, int attempt = 1)
{
    try
    {
        AddLog($"🔍 CheckSingleAppAvailability для {appId}, попытка {attempt}");
        
        var app = appManager.GetAllApps().FirstOrDefault(a => a.Id == appId);
        if (app == null)
        {
            AddLog($"   ❌ Приложение {appId} не найдено в appManager");
            return;
        }

        AddLog($"   📦 App из appManager: {app.DisplayName}");
        AddLog($"   🆔 Альтернативный ID: {app.AlternativeId ?? "НЕТ"}");
        AddLog($"   🔗 Ссылки: {(app.InstallerUrls.Any() ? string.Join(", ", app.InstallerUrls) : "нет")}");

        var availabilityResult = await availabilityChecker.CheckAppAvailabilityWithSize(app);
        var status = availabilityResult.Status;
        var size = availabilityResult.SizeMB;
        
        AddLog($"   Результат: {status}, Размер: {(size > 0 ? $"~{size} МБ" : "неизвестен")}");

        await Dispatcher.InvokeAsync(() =>
        {
            if (appCheckBoxes.TryGetValue(appId, out var checkBox))
            {
                availabilityStatus[appId] = status;
                
                var parentPanel = VisualTreeHelper.GetParent(checkBox) as Panel;
                if (parentPanel != null)
                {
                    var oldButton = parentPanel.Children
                        .OfType<Button>()
                        .FirstOrDefault(b => b.Tag?.ToString() == appId + "_suggest");
                    
                    if (oldButton != null)
                    {
                        parentPanel.Children.Remove(oldButton);
                    }
                }
                
                switch (status)
                {
                    case AvailabilityChecker.AvailabilityStatus.Available:
                        checkBox.Foreground = Brushes.LightGreen;
                        checkBox.ToolTip = $"✅ Доступно для установки ({(size > 0 ? $"~{size} МБ" : "размер неизвестен")})";
                        checkBox.IsEnabled = true;
                        AddLog($"✅ {app.DisplayName} доступен" + (size > 0 ? $" (~{size} МБ)" : ""));
                        break;
                        
                    case AvailabilityChecker.AvailabilityStatus.Unavailable:
                        if (attempt < 3)
                        {
                            checkBox.Foreground = Brushes.Gray;
                            checkBox.ToolTip = $"⏳ Повторная проверка... ({attempt}/3)";
                            AddLog($"⏳ Повторная проверка {app.DisplayName} через 2с");
                            _ = Task.Delay(2000).ContinueWith(_ => 
                                CheckSingleAppAvailability(appId, attempt + 1));
                        }
                        else
                        {
                            checkBox.Foreground = Brushes.LightCoral;
                            checkBox.ToolTip = "❌ Недоступно";
                            checkBox.IsEnabled = false;
                            AddLog($"❌ {app.DisplayName} недоступен");
                        }
                        break;
                        
                    default:
                        checkBox.Foreground = Brushes.Gray;
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

private void RefreshAllColors_Click(object sender, RoutedEventArgs e)
{
    AddLog("🔄 Принудительное обновление цветов...");
    
    foreach (var kvp in appCheckBoxes)
    {
        if (availabilityStatus.TryGetValue(kvp.Key, out var status))
        {
            var newColor = status == AvailabilityChecker.AvailabilityStatus.Available 
                ? Brushes.LightGreen 
                : Brushes.LightCoral;
            
            kvp.Value.Foreground = newColor;
            kvp.Value.InvalidateVisual();
            
            AddLog($"   {kvp.Key} -> {(newColor == Brushes.LightGreen ? "зелёный" : "красный")}");
        }
        else
        {
            AddLog($"   {kvp.Key} -> статус не найден, оставляем как есть");
        }
    }
    
    AddLog("✅ Обновление цветов завершено");
}

private void AddSuggestionButton(AppInfo app, CheckBox checkBox)
{
    // Находим родительскую панель (должен быть StackPanel)
    var parentPanel = VisualTreeHelper.GetParent(checkBox) as StackPanel;
    if (parentPanel == null) return;

    // Проверяем, не добавлена ли уже кнопка
    var existingButton = parentPanel.Children
        .OfType<Button>()
        .FirstOrDefault(b => b.Tag?.ToString() == app.Id + "_suggest");
    
    if (existingButton != null) return;

    // Создаём новую горизонтальную панель для этой программы
    var newRow = new StackPanel 
    { 
        Orientation = Orientation.Horizontal,
        Margin = new Thickness(0, 2, 0, 2)
    };

    // Создаём копию чекбокса
    var newCheckBox = new CheckBox
    {
        Content = checkBox.Content,
        Tag = checkBox.Tag,
        Foreground = checkBox.Foreground,
        Margin = new Thickness(0, 0, 5, 0),
        IsChecked = checkBox.IsChecked,
        ToolTip = checkBox.ToolTip,
        IsEnabled = checkBox.IsEnabled
    };

    // Создаём маленькую кнопку
    var suggestButton = new Button
    {
        Content = "🔄",
        Width = 20,
        Height = 20,
        Margin = new Thickness(0, 0, 0, 0),
        Tag = app.Id + "_suggest",
        Background = new SolidColorBrush(Color.FromRgb(255, 140, 0)),
        Foreground = Brushes.White,
        FontSize = 10,
        Padding = new Thickness(0),
        ToolTip = "Предложить альтернативный источник",
        Cursor = Cursors.Hand,
        VerticalAlignment = VerticalAlignment.Center
    };
    
    suggestButton.Click += async (s, e) => await SuggestAlternative(app);

    // Добавляем чекбокс и кнопку в новую строку
    newRow.Children.Add(newCheckBox);
    newRow.Children.Add(suggestButton);

    // Заменяем старый чекбокс новой строкой
    int index = parentPanel.Children.IndexOf(checkBox);
    parentPanel.Children.RemoveAt(index);
    parentPanel.Children.Insert(index, newRow);

    // Обновляем ссылку в словаре
    appCheckBoxes[app.Id] = newCheckBox;
}
public void AddLog(string message)
{
    Dispatcher.Invoke(() =>
    {
        txtInstallLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n");
        txtInstallLog.ScrollToEnd();
    });
}
        private async Task SuggestAlternative(AppInfo app)
        {
            try
            {
                AddLog($"🔍 Поиск альтернативы для: {app.DisplayName}");

                var dialog = new AlternativeSourceDialog(app.DisplayName)
                {
                    Owner = this
                };

                if (dialog.ShowDialog() == true)
                {
                    bool changes = false;

                    if (dialog.SelectedPackage != null)
                    {
                        AddLog($"DEBUG: Выбран пакет: {dialog.SelectedPackage.Id}");

                        appManager.SaveAlternativeSource(
                            app.Id,
                            dialog.SelectedPackage.Id,
                            null,
                            priority: dialog.UseWingetFirst);

                            
    // 🆕 ПРОВЕРЯЕМ, ЧТО СОХРАНИЛОСЬ
    var savedApp = appManager.GetAppById(app.Id);
    AddLog($"DEBUG: После сохранения AlternativeId = '{savedApp?.AlternativeId}'");

                        // ОТПРАВЛЯЕМ СТАТИСТИКУ
                        await StatsService.Instance.TrackOverrideAsync(app.Id, dialog.SelectedPackage.Id, null, false);

                        AddLog($"✅ Сохранён Winget ID: {dialog.SelectedPackage.Id} для {app.DisplayName}");
                        changes = true;
                    }

                    if (!string.IsNullOrEmpty(dialog.CustomUrl))
                    {
                        AddLog($"DEBUG: Указана ссылка: {dialog.CustomUrl}");

                        appManager.SaveAlternativeSource(
                            app.Id,
                            null,
                            dialog.CustomUrl,
                            priority: dialog.UseUrlFirst);

                        await StatsService.Instance.TrackOverrideAsync(app.Id, null, dialog.CustomUrl, false);

                        AddLog($"✅ Сохранена ссылка: {dialog.CustomUrl} для {app.DisplayName}");
                        changes = true;
                    }

                    if (changes)
                    {
                        if (availabilityStatus.ContainsKey(app.Id))
                            availabilityStatus.Remove(app.Id);

                        if (appCheckBoxes.TryGetValue(app.Id, out var checkBox))
                        {
                            checkBox.Foreground = Brushes.Gray;
                            checkBox.ToolTip = "⏳ Проверка...";
                            checkBox.IsEnabled = true;
                        }

                        await Task.Delay(500);
                        await CheckSingleAppAvailability(app.Id);
                    }
                }
            }
            catch (Exception ex)
            {
                AddLog($"❌ Ошибка при поиске альтернативы: {ex.Message}");
            }
        }
        private void LoadSavedSelection()
        {
            try
            {
                var saved = appManager.LoadSelectedApps();
                foreach (var id in saved)
                {
                    if (appCheckBoxes.TryGetValue(id, out var cb) && cb.IsEnabled)
                    {
                        cb.IsChecked = true;
                    }
                }
                LogMessage($"🔄 Загружено {saved.Count} сохранённых выборов");
            }
            catch (Exception ex)
            {
                LogMessage($"⚠️ Ошибка загрузки сохранённых выборов: {ex.Message}");
            }
        }




private async void RefreshAvailability_Click(object sender, RoutedEventArgs e)
{
    AddLog("🔄 Перезапущена проверка доступности");
    
    // Проверяем приложения из каталога
    if (_catalog != null)
    {
        foreach (var app in _catalog.Apps)
        {
            // Очищаем кеш
            if (availabilityStatus.ContainsKey(app.Id))
                availabilityStatus.Remove(app.Id);
            
            // Сбрасываем цвет
            if (appCheckBoxes.TryGetValue(app.Id, out var checkBox))
            {
                checkBox.Foreground = Brushes.Gray;
                checkBox.ToolTip = "⏳ Проверка...";
                checkBox.IsEnabled = true;
                checkBox.InvalidateVisual();
            }
            
            // Запускаем проверку
            _ = CheckAppAvailabilityFromCatalog(app);
        }
    }
    
    // Проверяем пользовательские приложения
    var userApps = appManager.GetAllApps().Where(a => a.IsUserAdded).ToList();
    foreach (var app in userApps)
    {
        if (availabilityStatus.ContainsKey(app.Id))
            availabilityStatus.Remove(app.Id);
        
        if (appCheckBoxes.TryGetValue(app.Id, out var checkBox))
        {
            checkBox.Foreground = Brushes.Gray;
            checkBox.ToolTip = "⏳ Проверка...";
            checkBox.IsEnabled = true;
            checkBox.InvalidateVisual();
        }
        
        _ = CheckSingleAppAvailability(app.Id);
    }
    
    AddLog("✅ Проверка запущена для всех приложений");
}
private void SyncCatalogToAppManager()
{
    if (_catalog == null) return;
    
    foreach (var catalogApp in _catalog.Apps)
    {
        var existing = appManager.GetAppById(catalogApp.Id);
        if (existing == null)
        {
            // Создаём AppInfo из каталога
            var appInfo = new AppInfo
            {
                Id = catalogApp.Id,
                DisplayName = catalogApp.Name,
                Category = GetCategoryFromString(catalogApp.Category),
                InstallerUrls = !string.IsNullOrEmpty(catalogApp.DownloadUrl) 
                    ? new List<string> { catalogApp.DownloadUrl } 
                    : new List<string>(),
                AlternativeId = catalogApp.WingetId,
                RequiredSpaceMB = ParseSizeToMB(catalogApp.Size),
                IsUserAdded = false
            };
            
            // Добавляем в appManager
            appManager.AddCatalogApp(appInfo);
            AddLog($"📦 Добавлено в appManager: {catalogApp.Name} (ID: {catalogApp.Id})");
        }
    }
}
private async void UpdateSpaceStatus()
{
    try
    {
        var selected = GetSelectedApps();
        long totalRequired = 0;
        
        // Собираем реальные размеры для выбранных приложений
        foreach (var appId in selected)
        {
            var app = appManager.GetAppById(appId);
            if (app != null)
            {
                var result = await availabilityChecker.CheckAppAvailabilityWithSize(app);
                if (result.Status == AvailabilityChecker.AvailabilityStatus.Available)
                {
                    totalRequired += result.SizeMB;
                }
                else
                {
                    // Если приложение недоступно, используем запасной размер
                    totalRequired += 100; // 100 МБ по умолчанию
                }
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
                
                // Предложение переключить диск...
                if (disk != systemDrive)
                {
                    var systemDiskInfo = new DriveInfo(systemDrive);
                    long systemAvailable = systemDiskInfo.AvailableFreeSpace / 1024 / 1024;
                    
                    if (systemAvailable >= totalRequired)
                    {
                        var result = MessageBox.Show(
                            $"На диске {disk} недостаточно места.\n\n" +
                            $"На системном диске {systemDrive} достаточно места ({systemAvailable} МБ).\n\n" +
                            $"Установить на системный диск?",
                            "Недостаточно места",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Question);
                        
                        if (result == MessageBoxResult.Yes)
                        {
                            var systemDiskItem = cmbAvailableDisks.Items
                                .OfType<dynamic>()
                                .FirstOrDefault(d => d.Name == systemDrive);
                            
                            if (systemDiskItem != null)
                            {
                                cmbAvailableDisks.SelectedValue = systemDrive;
                            }
                        }
                    }
                }
            }
        }
    }
    catch (Exception ex)
    {
        LogMessage($"⚠️ Ошибка проверки места: {ex.Message}");
    }
}

        private List<string> GetSelectedApps()
        {
            return appCheckBoxes
                .Where(kv => kv.Value.IsChecked == true && kv.Value.IsEnabled)
                .Select(kv => kv.Key)
                .ToList();
        }

        private bool IsRunAsAdmin()
        {
            var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }

        private void RestartAsAdmin()
        {
            var exeName = Process.GetCurrentProcess().MainModule?.FileName;
            if (exeName != null)
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exeName,
                    UseShellExecute = true,
                    Verb = "runas"
                };
                try { Process.Start(psi); } catch { }
            }
            Application.Current.Shutdown();
        }

        private void CancelInstall_Click(object sender, RoutedEventArgs e)
        {
            if (cancellationTokenSource != null && !isCancelled)
            {
                var result = MessageBox.Show(
                    "Вы действительно хотите прервать установку?",
                    "Подтверждение отмены",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    isCancelled = true;
                    cancellationTokenSource?.Cancel();
                    btnCancelInstall.IsEnabled = false;
                    txtOverallStatus.Text = "⏹️ Установка прервана";
                }
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

            var required = appManager.GetTotalRequiredSpace(selectedApps);
            string disk = selectedInstallDrive.TrimEnd('\\');
            var drive = new DriveInfo(disk);
            
            if (!drive.IsReady || drive.AvailableFreeSpace / 1024 / 1024 < required)
            {
                var result = MessageBox.Show(
                    $"Недостаточно места на диске {disk}!\n\n" +
                    $"Требуется: {required} МБ\n" +
                    $"Доступно: {drive.AvailableFreeSpace / 1024 / 1024} МБ\n\n" +
                    $"Всё равно продолжить?",
                    "Предупреждение",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                
                if (result == MessageBoxResult.No) return;
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
            var failedApps = new List<(string AppId, string DisplayName, string OriginalId, string OriginalUrl, bool IsUserAdded)>();

            LogMessage($"💾 Установка на диск: {selectedInstallDrive}");

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
                {
                    installProgressBar.Value = 100;
                }
                else
                {
                    installProgressBar.Value = progressDict.Values.Average(x => x.Percentage);
                }
            });

            txtOverallStatus.Text = $"⏳ Установка 0/{appsToInstall.Count}...";

            int completed = 0;
            int failed = 0;

            var tasks = appsToInstall.Select(app => Task.Run(async () =>
            {
                await installSemaphore.WaitAsync();
                try
                {
                    if (token.IsCancellationRequested) return;

                    // ВАЖНО: передаём 5 параметров, включая installDrive
                    var result = await installService.InstallAppAsync(app, wingetSources, token, progress, selectedInstallDrive);
                    
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (result.Success)
                        {
                            completed++;
                            if (appCheckBoxes.TryGetValue(app.Id, out var cb))
                            {
                                cb.Foreground = new SolidColorBrush(Color.FromRgb(136, 136, 136));
                                cb.Opacity = 0.7;
                                cb.IsEnabled = false;
                                cb.ToolTip = "✅ Установлено";
                            }
                        }
                        else
                        {
                            failed++;
                            string originalId = app.Id.StartsWith("User.") ? "" : app.Id;
                            failedApps.Add((app.Id, app.DisplayName, originalId, app.InstallerUrls.FirstOrDefault() ?? "", app.IsUserAdded));
                        }
                        txtOverallStatus.Text = $"⏳ Установка: {completed + failed}/{appsToInstall.Count} (✅ {completed} | ❌ {failed})";
                    });
                }
                finally
                {
                    installSemaphore.Release();
                }
            }, token));

            try
            {
                await Task.WhenAll(tasks);
                
                if (!isCancelled)
                {
                    if (failedApps.Any())
                    {
                        LogMessage($"⚠️ Обнаружено {failedApps.Count} проблемных установок. Показываем диалог...");
                        
                        var dialog = new BulkFallbackDialog(failedApps) { Owner = this };
                        
                        if (dialog.ShowDialog() == true && dialog.Applied)
                        {
                            bool remember = dialog.RememberChoices;
                            
                            var toRemove = dialog.FailedApps
                                .Where(x => x.Skip && x.RemoveFromCatalog)
                                .ToList();
                            
                            foreach (var item in toRemove)
                            {
                                var app = allApps.FirstOrDefault(a => a.Id == item.AppId);
                                if (app != null)
                                {
                                    if (app.IsUserAdded)
                                    {
                                        appManager.RemoveUserApp(item.AppId);
                                        LogMessage($"🗑️ Удалено пользовательское приложение: {item.DisplayName}");
                                        
                                        if (categoryPanels.TryGetValue(app.Category, out var panel))
                                        {
                                            var toRemoveFromUI = panel.Children
                                                .OfType<StackPanel>()
                                                .FirstOrDefault(sp => sp.Children
                                                    .OfType<CheckBox>()
                                                    .Any(cb => cb.Tag?.ToString() == item.AppId));
                                            
                                            if (toRemoveFromUI != null)
                                                panel.Children.Remove(toRemoveFromUI);
                                        }
                                        
                                        appCheckBoxes.Remove(item.AppId);
                                        availabilityStatus.Remove(item.AppId);
                                    }
                                    else
                                    {
                                        appManager.HideStandardApp(item.AppId);
                                        LogMessage($"👻 Скрыто стандартное приложение: {item.DisplayName}");
                                        
                                        if (categoryPanels.TryGetValue(app.Category, out var panel))
                                        {
                                            var toRemoveFromUI = panel.Children
                                                .OfType<StackPanel>()
                                                .FirstOrDefault(sp => sp.Children
                                                    .OfType<CheckBox>()
                                                    .Any(cb => cb.Tag?.ToString() == item.AppId));
                                            
                                            if (toRemoveFromUI != null)
                                                panel.Children.Remove(toRemoveFromUI);
                                        }
                                        
                                        appCheckBoxes.Remove(item.AppId);
                                        availabilityStatus.Remove(item.AppId);
                                    }
                                }
                            }
                            
                            var wingetSelected = dialog.FailedApps
                                .Where(x => x.SearchWinget && x.SelectedPackage != null)
                                .ToList();
                            
                            var urlSelected = dialog.FailedApps
                                .Where(x => x.ReplaceWithUrl && !string.IsNullOrWhiteSpace(x.NewUrl))
                                .ToList();
                            
                            if (remember)
                            {
                                foreach (var item in wingetSelected)
                                {
                                    appManager.SaveAlternativeSource(
                                        item.AppId,
                                        item.SelectedPackage!.Id,
                                        null);
                                    LogMessage($"💾 Сохранён альтернативный Winget ID для {item.DisplayName}: {item.SelectedPackage!.Id}");
                                }
                                
                                foreach (var item in urlSelected)
                                {
                                    appManager.SaveAlternativeSource(
                                        item.AppId,
                                        null,
                                        item.NewUrl);
                                    LogMessage($"💾 Сохранена альтернативная ссылка для {item.DisplayName}: {item.NewUrl}");
                                }
                            }
                            
                            var appsToRetry = allApps.Where(a => 
                                wingetSelected.Select(x => x.AppId).Contains(a.Id) || 
                                urlSelected.Select(x => x.AppId).Contains(a.Id)).ToList();
                            
                            if (appsToRetry.Any())
                            {
                                LogMessage($"🔄 Повторная попытка установки {appsToRetry.Count} программ с новыми источниками...");
                                
                                var retryApps = new List<AppInfo>();
                                
                                foreach (var app in appsToRetry)
                                {
                                    var wingetItem = wingetSelected.FirstOrDefault(x => x.AppId == app.Id);
                                    var urlItem = urlSelected.FirstOrDefault(x => x.AppId == app.Id);
                                    
                                    var tempApp = new AppInfo
                                    {
                                        Id = app.Id,
                                        DisplayName = app.DisplayName,
                                        Category = app.Category,
                                        SilentArgs = app.SilentArgs,
                                        RequiredSpaceMB = app.RequiredSpaceMB,
                                        InstallerUrls = new List<string>(app.InstallerUrls),
                                        AlternativeId = wingetItem?.SelectedPackage?.Id ?? app.AlternativeId
                                    };
                                    
                                    if (urlItem != null && !tempApp.InstallerUrls.Contains(urlItem.NewUrl))
                                    {
                                        tempApp.InstallerUrls.Insert(0, urlItem.NewUrl);
                                    }
                                    
                                    retryApps.Add(tempApp);
                                }
                                
                                await RetryFailedApps(retryApps, token, progress);
                            }
                            
                            var skipped = dialog.FailedApps.Count(x => x.Skip && !x.RemoveFromCatalog);
                            if (skipped > 0)
                                LogMessage($"⏭️ Пропущено программ: {skipped}");
                        }
                    }
                    
                    txtOverallStatus.Text = $"✅ Установка завершена. Успешно: {completed}, ошибок: {failed}";
                    appManager.SaveSelectedApps(GetSelectedApps());
                }
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

        private async Task RetryFailedApps(List<AppInfo> appsToRetry, CancellationToken token, IProgress<AppInstallProgress> progress)
        {
            int retryCompleted = 0;
            int retryFailed = 0;
            
            var retryTasks = appsToRetry.Select(app => Task.Run(async () =>
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
                            retryCompleted++;
                            
                            if (app.AlternativeId != null || app.InstallerUrls.Count > 0)
                            {
                                appManager.IncrementAlternativeSuccess(app.Id);
                            }
                            
                            if (appCheckBoxes.TryGetValue(app.Id, out var cb))
                            {
                                cb.Foreground = new SolidColorBrush(Color.FromRgb(136, 136, 136));
                                cb.Opacity = 0.7;
                                cb.IsEnabled = false;
                                cb.ToolTip = "✅ Установлено";
                            }
                        }
                        else
                        {
                            retryFailed++;
                        }
                    });
                }
                finally
                {
                    installSemaphore.Release();
                }
            }, token));

            await Task.WhenAll(retryTasks);
            LogMessage($"🔄 Повторная установка: {retryCompleted} успешно, {retryFailed} ошибок");
        }

        private async void BtnAddApp_Click(object sender, RoutedEventArgs e)
        {
            // 1. Проверяем, нужно ли спрашивать согласие
            var consentService = new ConsentService();
            var shouldAsk = await consentService.ShouldAskForConsentAsync();

            bool allowStats = true;

            if (shouldAsk)
            {
                var dialog = new Views.ConsentDialog();
                dialog.Owner = this;

                if (dialog.ShowDialog() == true)
                {
                    allowStats = dialog.AllowStats;
                    await consentService.SaveConsentAsync(allowStats);
                }
                else
                {
                    return;
                }
            }
            else
            {
                allowStats = await consentService.IsStatsAllowedAsync();
            }

            // 2. Открываем диалог добавления
            var addDialog = new AddAppDialog { Owner = this };
            if (addDialog.ShowDialog() == true && addDialog.Result != null)
            {
                var newApp = addDialog.Result;

                // 3. Добавляем в user.json
                appManager.AddUserApp(newApp);

                // 4. Добавляем в UI (используем общий метод)
                AddUserAppToUI(newApp);

                // 5. Отправляем статистику, если разрешено
                if (allowStats)
                {
                    // Определяем Winget ID для статистики
                    string? wingetId = null;

                    // Пытаемся найти приложение в основном каталоге
                    if (_catalog != null)
                    {
                        var catalogApp = _catalog?.Apps.FirstOrDefault(a => a.Name == newApp.DisplayName);
                        if (catalogApp != null && !string.IsNullOrEmpty(catalogApp.WingetId))
                        {
                            wingetId = catalogApp.WingetId;
                        }
                    }

                    // Если не нашли в каталоге, используем AlternativeId
                    if (string.IsNullOrEmpty(wingetId) && !string.IsNullOrEmpty(newApp.AlternativeId))
                    {
                        wingetId = newApp.AlternativeId;
                    }

                    // Используем синглтон StatsService.Instance
                    await StatsService.Instance.TrackUserAddAsync(
                        newApp.Id,
                        wingetId,
                        newApp.InstallerUrls.FirstOrDefault()
                    );

                    LogMessage($"📊 Статистика отправлена" + (!string.IsNullOrEmpty(wingetId) ? $" (Winget ID: {wingetId})" : ""));
                }
                else
                {
                    LogMessage("🔒 Статистика не отправляется (вы отказались)");
                }

                LogMessage($"➕ Добавлено пользовательское приложение: {newApp.DisplayName}");
            }
        }
        private void RemoveUserApp(string appId)
        {
            var result = MessageBox.Show("Удалить приложение из списка?", "Подтверждение", 
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            
            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    var appName = "неизвестно";
                    if (appCheckBoxes.TryGetValue(appId, out var cb))
                    {
                        appName = cb.Content.ToString() ?? "неизвестно";
                    }
                    
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
                    
                    LogMessage($"🗑️ Удалено пользовательское приложение: {appName}");
                }
                catch (Exception ex)
                {
                    LogMessage($"❌ Ошибка при удалении: {ex.Message}");
                }
            }
        }

        private void BtnRemoveAllUserApps_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                "Вы действительно хотите удалить ВСЕ пользовательские приложения?\n\n" +
                "Это действие нельзя отменить.", 
                "Подтверждение удаления", 
                MessageBoxButton.YesNo, 
                MessageBoxImage.Question);
            
            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    var userAppIds = appCheckBoxes
                        .Where(kv => kv.Value.Foreground == Brushes.Orange)
                        .Select(kv => kv.Key)
                        .ToList();
                    
                    if (userAppIds.Count == 0)
                    {
                        MessageBox.Show("Нет пользовательских приложений для удаления.", 
                            "Информация", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }
                    
                    foreach (var appId in userAppIds)
                    {
                        appManager.RemoveUserApp(appId);
                    }
                    
                    PanelПользовательские.Children.Clear();
                    
                    foreach (var appId in userAppIds)
                    {
                        appCheckBoxes.Remove(appId);
                        availabilityStatus.Remove(appId);
                    }
                    
                    LogMessage($"✅ Удалено {userAppIds.Count} пользовательских приложений");
                    
                    MessageBox.Show($"Удалено {userAppIds.Count} пользовательских приложений.", 
                        "Готово", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Ошибка при удалении: {ex.Message}", 
                        "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void BtnClearAllUserApps_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                "Вы действительно хотите удалить ВСЕ пользовательские приложения?\n\n" +
                "Это полностью очистит файл apps.json. Отменить действие будет нельзя.",
                "Полная очистка",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            
            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    string configPath;
                    string appDataPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Ven4Tools");
                    
                    string exeDir = AppDomain.CurrentDomain.BaseDirectory;
                    string portableMarker = Path.Combine(exeDir, "portable.dat");
                    
                    if (File.Exists(portableMarker))
                    {
                        configPath = Path.Combine(exeDir, "Data", "apps.json");
                    }
                    else
                    {
                        configPath = Path.Combine(appDataPath, "apps.json");
                    }
                    
                    if (File.Exists(configPath))
                    {
                        File.Delete(configPath);
                        LogMessage($"🗑️ Файл {configPath} удалён");
                    }
                    
                    string selectionPath = configPath + ".selection";
                    if (File.Exists(selectionPath))
                    {
                        File.Delete(selectionPath);
                        LogMessage($"🗑️ Файл {selectionPath} удалён");
                    }
                    
                    LogMessage($"✅ Файл пользовательских приложений очищен");
                    
                    var restartResult = MessageBox.Show(
                        "Для применения изменений рекомендуется перезапустить программу.\n\nПерезапустить сейчас?",
                        "Перезапуск",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);
                    
                    if (restartResult == MessageBoxResult.Yes)
                    {
                        RestartApplication();
                    }
                    else
                    {
                        ReloadAllApps();
                        MessageBox.Show("Все пользовательские приложения удалены.", 
                            "Готово", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Ошибка при удалении: {ex.Message}", 
                        "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void BtnInstallOffice_Click(object sender, RoutedEventArgs e)
        {
            var officeWindow = new OfficeWindow(
                new Dictionary<string, string>
                {
                    { "Office 365 ProPlus", "O365ProPlusRetail" },
                    { "Office 2024 ProPlus", "ProPlus2024Retail" },
                    { "Office 2021 Professional", "Professional2021Retail" },
                    { "Office 2019 Professional", "Professional2019Retail" }
                },
                new[] { "ru-ru", "en-us", "de-de", "fr-fr" },
                this);
            
            officeWindow.Owner = this;
            officeWindow.ShowDialog();
        }

        #region Turbo Boost

        private int GetTurboBoostAttributes()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(TurboBoostRegPath))
                {
                    if (key == null) return -1;
                    var val = key.GetValue("Attributes");
                    return val != null ? Convert.ToInt32(val) : -1;
                }
            }
            catch (Exception ex)
            {
                LogMessage($"⚠️ Ошибка получения атрибутов турбобуста: {ex.Message}");
                return -1;
            }
        }

        private void SetTurboBoostAttributes(int value)
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(TurboBoostRegPath, writable: true))
                {
                    if (key == null)
                    {
                        using (var newKey = Registry.LocalMachine.CreateSubKey(TurboBoostRegPath))
                        {
                            newKey.SetValue("Attributes", value, RegistryValueKind.DWord);
                        }
                    }
                    else
                    {
                        key.SetValue("Attributes", value, RegistryValueKind.DWord);
                    }
                }
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Ошибка установки атрибутов турбобуста: {ex.Message}");
                throw;
            }
        }

        private void BtnDisableTurboBoost_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SetTurboBoostAttributes(2);
                MessageBox.Show(
                    "✅ Турбобуст отключён (Attributes = 2).\n\nПосле перезагрузки в настройках электропитания появится пункт управления.",
                    "Успех",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                LogMessage("⚡ Турбобуст отключён (Attributes=2)");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"❌ Ошибка: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                LogMessage($"❌ Ошибка при отключении турбобуста: {ex.Message}");
            }
        }

        private void BtnEnableTurboBoost_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SetTurboBoostAttributes(1);
                MessageBox.Show(
                    "✅ Турбобуст включён (Attributes = 1).\n\nПосле перезагрузки пункт управления Turbo Boost вернётся к стандартному поведению.",
                    "Успех",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                LogMessage("⚡ Турбобуст включён (Attributes=1)");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"❌ Ошибка: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                LogMessage($"❌ Ошибка при включении турбобуста: {ex.Message}");
            }
        }

        #endregion

        #region Активация MAS

        private void BtnRunActivator_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string command = "irm https://get.activated.win | iex";
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoExit -Command \"{command}\"",
                    UseShellExecute = true,
                    Verb = "runas"
                };
                Process.Start(psi);
                LogMessage("🔑 Запущен интерактивный активатор MAS");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                LogMessage($"❌ Ошибка запуска активатора: {ex.Message}");
            }
        }

        private void RunActivation_Click(object sender, RoutedEventArgs e)
        {
            if (cmbActivationType.SelectedItem is ComboBoxItem selectedItem && selectedItem.Tag is string parameter)
            {
                RunActivationCommand(parameter);
            }
            else
            {
                MessageBox.Show("Выберите тип активации.", "Внимание", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void RunActivationCommand(string parameter)
        {
            try
            {
                string command = $"& ([ScriptBlock]::Create((curl.exe -s --doh-url https://1.1.1.1/dns-query https://get.activated.win | Out-String))) {parameter}";
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoExit -Command \"{command}\"",
                    UseShellExecute = true,
                    Verb = "runas"
                };
                Process.Start(psi);
                LogMessage($"🔑 Запущена активация с параметром {parameter}");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                LogMessage($"❌ Ошибка активации: {ex.Message}");
            }
        }

        #endregion

        #region Проверка обновлений



#endregion

        #region Перезапуск и перезагрузка

        private void RestartApplication()
        {
            try
            {
                string? executablePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(executablePath))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = executablePath,
                        UseShellExecute = true,
                        Verb = "runas"
                    });
                    Application.Current.Shutdown();
                }
                else
                {
                    LogMessage("❌ Не удалось определить путь для перезапуска");
                }
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Ошибка при перезапуске: {ex.Message}");
                MessageBox.Show($"Не удалось перезапустить программу: {ex.Message}", 
                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ReloadAllApps()
        {
            try
            {
                PanelБраузеры.Children.Clear();
                PanelОфис.Children.Clear();
                PanelГрафика.Children.Clear();
                PanelРазработка.Children.Clear();
                PanelМессенджеры.Children.Clear();
                PanelМультимедиа.Children.Clear();
                PanelСистемные.Children.Clear();
                PanelИгровыеСервисы.Children.Clear();
                PanelДрайверпаки.Children.Clear();
                PanelДругое.Children.Clear();
                PanelПользовательские.Children.Clear();
                
                appCheckBoxes.Clear();
                availabilityStatus.Clear();
                
                LoadApps();
                
                LogMessage("🔄 Список приложений перезагружен");
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Ошибка при перезагрузке: {ex.Message}");
            }
        }

        #endregion
    }
}
