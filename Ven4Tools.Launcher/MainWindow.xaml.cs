using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using Ven4Tools.Launcher.Models;
using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Launcher;

public partial class MainWindow : Window
{
    private readonly UpdateService _updateService;
    private string? _installPath;
    private UpdateInfo? _currentUpdateInfo;
    private bool _isInstalling = false;
    
    // Ключ реестра для сохранения пути (для установленной версии)
    private const string RegistryKey = @"Software\Ven4Tools";
    private const string RegistryValue = "InstallPath";
    
    public MainWindow()
    {
        InitializeComponent();
        _updateService = new UpdateService();
        
        // Загружаем сохранённый путь
        _installPath = LoadInstallPath();
        
        AddLog($"Лаунчер запущен из: {AppDomain.CurrentDomain.BaseDirectory}");
        AddLog($"Сохранённый путь: {_installPath ?? "не задан"}");
        
        Loaded += async (s, e) => await InitializeAsync();
    }
    
    /// <summary>
    /// Загружает сохранённый путь установки клиента
    /// </summary>
    private string? LoadInstallPath()
    {
        // Проверяем портативный режим
        var portableMarker = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Portable.dat");
        if (File.Exists(portableMarker))
        {
            // Портативный режим — путь в файле рядом с лаунчером
            var pathFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Portable.path");
            if (File.Exists(pathFile))
            {
                var path = File.ReadAllText(pathFile).Trim();
                if (Directory.Exists(path))
                    return path;
            }
            return null;
        }
        
        // Установленный режим — читаем из реестра
        try
        {
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RegistryKey))
            {
                var path = key?.GetValue(RegistryValue)?.ToString();
                if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                    return path;
            }
        }
        catch { }
        
        return null;
    }
    
    /// <summary>
    /// Сохраняет путь установки клиента
    /// </summary>
    private void SaveInstallPath(string path)
    {
        // Проверяем портативный режим
        var portableMarker = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Portable.dat");
        if (File.Exists(portableMarker))
        {
            var pathFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Portable.path");
            File.WriteAllText(pathFile, path);
            AddLog($"✅ Путь сохранён (портативный): {path}");
            return;
        }
        
        // Установленный режим — пишем в реестр
        try
        {
            using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RegistryKey))
            {
                key?.SetValue(RegistryValue, path);
                AddLog($"✅ Путь сохранён в реестр: {path}");
            }
        }
        catch (Exception ex)
        {
            AddLog($"❌ Ошибка сохранения пути: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Проверяет, существует ли клиент по указанному пути
    /// </summary>
    private bool IsClientExists(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        return File.Exists(Path.Combine(path, "Ven4Tools.exe"));
    }
    
    private async Task InitializeAsync()
    {
        try
        {
            AddLog("Проверка обновлений...");
            
            // Проверяем, есть ли клиент по сохранённому пути
            bool clientExists = IsClientExists(_installPath);
            
            // Получаем путь к клиенту (если есть)
            string? clientPath = clientExists && _installPath != null
                ? Path.Combine(_installPath, "Ven4Tools.exe")
                : null;
            
            _currentUpdateInfo = await _updateService.CheckForUpdateAsync(clientPath);
            
            if (_currentUpdateInfo == null)
            {
                AddLog("Не удалось получить информацию");
                StatusText.Text = "Ошибка проверки";
                return;
            }
            
            // Всегда показываем кнопку выбора папки
            BtnSelectPath.Visibility = Visibility.Visible;
            
if (!clientExists)
{
    StatusText.Text = "Клиент не установлен.\n\n1. Нажмите 'Выбрать папку'\n2. Выберите место (например, D:\\)\n3. Лаунчер создаст папку Ven4Tools\n4. Нажмите 'Установить'";
    AddLog("Клиент не найден. Выберите папку для установки.");
                
                BtnInstall.Visibility = Visibility.Visible;
                BtnUpdate.Visibility = Visibility.Collapsed;
                BtnLaunch.Visibility = Visibility.Collapsed;
                StatusText.Foreground = System.Windows.Media.Brushes.Orange;
            }
            else if (_currentUpdateInfo.HasUpdate)
            {
                StatusText.Text = $"Доступно обновление {_currentUpdateInfo.LatestVersion} (текущая: {_currentUpdateInfo.CurrentVersion})";
                AddLog($"Текущая версия: {_currentUpdateInfo.CurrentVersion}");
                AddLog($"Новая версия: {_currentUpdateInfo.LatestVersion}");
                AddLog($"Путь: {_installPath}");
                
                BtnInstall.Visibility = Visibility.Collapsed;
                BtnUpdate.Visibility = Visibility.Visible;
                BtnLaunch.Visibility = Visibility.Visible;
                StatusText.Foreground = System.Windows.Media.Brushes.LightBlue;
            }
            else
            {
                StatusText.Text = $"Ven4Tools готов к запуску (версия {_currentUpdateInfo.CurrentVersion})";
                AddLog($"Версия: {_currentUpdateInfo.CurrentVersion}");
                AddLog($"Путь: {_installPath}");
                
                BtnInstall.Visibility = Visibility.Collapsed;
                BtnUpdate.Visibility = Visibility.Collapsed;
                BtnLaunch.Visibility = Visibility.Visible;
                StatusText.Foreground = System.Windows.Media.Brushes.LightGreen;
            }
            
            ProgressBar.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Ошибка проверки";
            AddLog($"Ошибка: {ex.Message}");
            StatusText.Foreground = System.Windows.Media.Brushes.Red;
        }
    }
    
private void BtnSelectPath_Click(object sender, RoutedEventArgs e)
{
    var dialog = new Microsoft.Win32.OpenFolderDialog();
    dialog.Title = "Выберите место для установки Ven4Tools\n(будет создана папка Ven4Tools)";
    dialog.Multiselect = false;

    if (dialog.ShowDialog() == true)
    {
        // Формируем конечный путь: выбранная_папка\Ven4Tools
        string parentPath = dialog.FolderName;
        _installPath = Path.Combine(parentPath, "Ven4Tools");

        // Создаём папку (и родительскую, если нужно)
        try
        {
            if (!Directory.Exists(_installPath))
            {
                Directory.CreateDirectory(_installPath);
                AddLog($"📁 Папка создана: {_installPath}");
            }
            else
            {
                AddLog($"📁 Папка уже существует: {_installPath}");
            }
        }
        catch (Exception ex)
        {
            AddLog($"❌ Не удалось создать папку: {ex.Message}");
            MessageBox.Show($"Не удалось создать папку:\n{ex.Message}", "Ошибка",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        SaveInstallPath(_installPath);
        AddLog($"✅ Выбран путь: {_installPath}");
        _ = InitializeAsync();
    }
}
    
    private async void BtnInstall_Click(object sender, RoutedEventArgs e)
    {
        if (_isInstalling) return;
        
        // Если путь не выбран — предлагаем выбрать
        if (string.IsNullOrEmpty(_installPath))
        {
            BtnSelectPath_Click(sender, e);
            if (string.IsNullOrEmpty(_installPath)) return;
        }
        
        _isInstalling = true;
        ProgressBar.Visibility = Visibility.Visible;
        ProgressBar.IsIndeterminate = false;
        ProgressBar.Value = 0;
        ProgressBar.Maximum = 100;
        
        try
        {
            AddLog($"Начинаем установку клиента в: {_installPath}");
            StatusText.Text = "Установка...";
            
            var progress = new Progress<string>(msg =>
            {
                Dispatcher.Invoke(() =>
                {
                    StatusText.Text = msg;
                    AddLog(msg);
                });
            });
            
            var success = await _updateService.InstallClientAsync(_installPath!, progress);
            
            if (success)
            {
                ProgressBar.Value = 100;
                StatusText.Text = "Установка завершена";
                AddLog("✅ Клиент успешно установлен");
                
                await InitializeAsync();
                
                var result = MessageBox.Show(
                    "Клиент успешно установлен.\n\nЗапустить Ven4Tools сейчас?",
                    "Установка завершена",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                
                if (result == MessageBoxResult.Yes)
                {
                    LaunchClient();
                    Close();
                }
            }
            else
            {
                AddLog("❌ Ошибка установки клиента");
                StatusText.Text = "Ошибка установки";
                MessageBox.Show("Не удалось установить клиент.\nПроверьте интернет-соединение и попробуйте снова.",
                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            AddLog($"❌ Ошибка: {ex.Message}");
            StatusText.Text = "Ошибка";
        }
        finally
        {
            ProgressBar.Visibility = Visibility.Collapsed;
            _isInstalling = false;
        }
    }
    
    private async void BtnUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_isInstalling) return;
        
        _isInstalling = true;
        ProgressBar.Visibility = Visibility.Visible;
        ProgressBar.IsIndeterminate = false;
        ProgressBar.Value = 0;
        ProgressBar.Maximum = 100;
        
        try
        {
            AddLog($"Начинаем обновление клиента в: {_installPath}");
            StatusText.Text = "Обновление...";
            
            var progress = new Progress<string>(msg =>
            {
                Dispatcher.Invoke(() =>
                {
                    StatusText.Text = msg;
                    AddLog(msg);
                });
            });
            
            var success = await _updateService.InstallClientAsync(_installPath!, progress);
            
            if (success)
            {
                ProgressBar.Value = 100;
                StatusText.Text = "Обновление завершено";
                AddLog("✅ Клиент успешно обновлён");
                
                await InitializeAsync();
                
                var result = MessageBox.Show(
                    "Клиент успешно обновлён.\n\nЗапустить Ven4Tools сейчас?",
                    "Обновление завершено",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                
                if (result == MessageBoxResult.Yes)
                {
                    LaunchClient();
                    Close();
                }
            }
            else
            {
                AddLog("❌ Ошибка обновления клиента");
                StatusText.Text = "Ошибка обновления";
                MessageBox.Show("Не удалось обновить клиент.\nПроверьте интернет-соединение и попробуйте снова.",
                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            AddLog($"❌ Ошибка: {ex.Message}");
            StatusText.Text = "Ошибка";
        }
        finally
        {
            ProgressBar.Visibility = Visibility.Collapsed;
            _isInstalling = false;
        }
    }
    
    private void LaunchClient()
    {
        try
        {
            if (string.IsNullOrEmpty(_installPath)) return;
            
            var clientPath = Path.Combine(_installPath, "Ven4Tools.exe");
            if (!File.Exists(clientPath))
            {
                AddLog("Клиент не найден");
                return;
            }
            
            AddLog($"Запуск Ven4Tools из: {clientPath}");
            
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = clientPath,
                UseShellExecute = true,
                Verb = "runas"
            });
            
            if (process != null)
            {
                AddLog($"Процесс запущен (ID: {process.Id})");
            }
        }
        catch (Exception ex)
        {
            AddLog($"Ошибка запуска: {ex.Message}");
        }
    }
    
    private async void BtnLaunch_Click(object sender, RoutedEventArgs e)
    {
        LaunchClient();
        await Task.Delay(500);
        Close();
    }
    
    private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        AddLog("Обновление статуса...");
        await InitializeAsync();
    }
    
    private void BtnFeedback_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var appVersion = "2.3.0";
            var osVersion = Environment.OSVersion.VersionString;
            
            var title = Uri.EscapeDataString($"Лаунчер: обратная связь");
            var body = Uri.EscapeDataString(
                $"## Версия лаунчера\n{appVersion}\n\n" +
                $"## ОС\n{osVersion}\n\n" +
                $"## Описание\n\n" +
                $"### Что случилось?\n\n" +
                $"### Как воспроизвести?\n\n" +
                $"### Ожидаемое поведение\n\n");
            
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
            AddLog($"❌ Ошибка: {ex.Message}");
            MessageBox.Show("Не удалось открыть форму обратной связи.\n" +
                            "Пожалуйста, напишите на GitHub вручную:\n" +
                            "https://github.com/Ven4ru/Ven4Tools/issues",
                            "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
    
    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        AddLog("Закрытие лаунчера");
        Close();
    }
    
    private void AddLog(string message)
    {
        Dispatcher.Invoke(() =>
        {
            LogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n");
            LogTextBox.ScrollToEnd();
        });
    }
}