using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Ven4Tools.Launcher.Models;
using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Launcher;

public partial class MainWindow : Window
{
    private readonly UpdateService _updateService;
    private string? _mainAppPath;
    private UpdateInfo? _currentUpdateInfo;
    private bool _isInstalling = false;
    
    public MainWindow()
    {
        InitializeComponent();
        _updateService = new UpdateService();
        _mainAppPath = FindVen4Tools();
        
        AddLog($"Лаунчер запущен, путь: {AppDomain.CurrentDomain.BaseDirectory}");
        AddLog($"Программа найдена: {_mainAppPath ?? "нет"}");
        
        Loaded += async (s, e) => await InitializeAsync();
    }
    
    private string? FindVen4Tools()
    {
        string[] paths = {
            @"C:\Program Files\Ven4Tools\Ven4Tools.exe",
            @"C:\Program Files (x86)\Ven4Tools\Ven4Tools.exe",
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Ven4Tools.exe"),
            @"C:\Ven4Tools_New\Ven4Tools.exe"
        };
        
        foreach (string path in paths)
        {
            if (File.Exists(path))
            {
                AddLog($"Найдена программа: {path}");
                return path;
            }
        }
        
        AddLog("Программа не найдена");
        return null;
    }
    
    private async Task InitializeAsync()
    {
        try
        {
            AddLog("Проверка обновлений...");
            
            if (_currentUpdateInfo == null)
            {
                _currentUpdateInfo = await _updateService.CheckForUpdateAsync(_mainAppPath);
            }
            
            if (_currentUpdateInfo == null)
            {
                AddLog("Не удалось получить информацию");
                StatusText.Text = "Ошибка проверки";
                return;
            }
            
            if (!_currentUpdateInfo.IsInstalled)
            {
                StatusText.Text = "Ven4Tools не установлен";
                AddLog("Программа не найдена. Нажмите 'Установить' для загрузки.");
                StatusText.Foreground = System.Windows.Media.Brushes.Orange;
            }
            else if (_currentUpdateInfo.HasUpdate)
            {
                StatusText.Text = $"Доступно обновление {_currentUpdateInfo.LatestVersion}";
                AddLog($"Текущая версия: {_currentUpdateInfo.CurrentVersion}");
                AddLog($"Новая версия: {_currentUpdateInfo.LatestVersion}");
                StatusText.Foreground = System.Windows.Media.Brushes.LightBlue;
            }
            else
            {
                StatusText.Text = "Ven4Tools готов к запуску";
                AddLog($"Версия: {_currentUpdateInfo.CurrentVersion}");
                AddLog("Нажмите 'Запустить' для старта программы.");
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
    
    private async void BtnInstall_Click(object sender, RoutedEventArgs e)
    {
        if (_isInstalling) return;
        
        _isInstalling = true;
        ProgressBar.Visibility = Visibility.Visible;
        ProgressBar.IsIndeterminate = false;
        ProgressBar.Value = 0;
        ProgressBar.Maximum = 100;
        
        try
        {
            AddLog("Начинаем процесс установки...");
            
            if (_currentUpdateInfo == null)
            {
                _currentUpdateInfo = await _updateService.CheckForUpdateAsync(_mainAppPath);
                if (_currentUpdateInfo == null || string.IsNullOrEmpty(_currentUpdateInfo.DownloadUrl))
                {
                    AddLog("Не удалось получить ссылку для скачивания");
                    StatusText.Text = "Ошибка: нет ссылки";
                    return;
                }
            }
            
            var tempFile = Path.Combine(Path.GetTempPath(), $"Ven4Tools_Setup_{DateTime.Now:yyyyMMdd_HHmmss}.exe");
            AddLog($"Временный файл: {Path.GetFileName(tempFile)}");
            
            var lastUpdateTime = DateTime.Now;
            var lastPercent = 0;
            
            var progress = new Progress<double>(p =>
            {
                var percent = (int)(p * 100);
                var now = DateTime.Now;
                
                if ((now - lastUpdateTime).TotalMilliseconds >= 100 || percent >= 100)
                {
                    lastUpdateTime = now;
                    Dispatcher.Invoke(() =>
                    {
                        if (percent != lastPercent)
                        {
                            lastPercent = percent;
                            ProgressBar.Value = percent;
                            StatusText.Text = $"Скачивание: {percent}%";
                            
                            if (percent % 25 == 0 || percent == 100)
                            {
                                AddLog($"Загружено: {percent}%");
                            }
                        }
                    });
                }
            });
            
            AddLog("Начинаю загрузку установщика...");
            StatusText.Text = "Скачивание: 0%";
            
            var success = await _updateService.DownloadAndInstallAsync(
                _currentUpdateInfo.DownloadUrl, 
                tempFile, 
                progress);
            
            if (success)
            {
                ProgressBar.Value = 100;
                StatusText.Text = "Скачивание: 100%";
                
                ProgressBar.IsIndeterminate = true;
                StatusText.Text = "Установка...";
                AddLog("Установщик запущен! Ждем завершения установки...");
                
                await Task.Delay(5000);
                
                _mainAppPath = FindVen4Tools();
                _currentUpdateInfo = await _updateService.CheckForUpdateAsync(_mainAppPath);
                
                if (_currentUpdateInfo != null && _currentUpdateInfo.IsInstalled)
                {
                    AddLog($"Программа успешно установлена!");
                    AddLog($"Путь: {_mainAppPath}");
                    StatusText.Text = "Установка завершена";
                    ProgressBar.Visibility = Visibility.Collapsed;
                    await InitializeAsync();
                }
                else
                {
                    AddLog("Программа не обнаружена после установки");
                    AddLog("Нажмите 'Обновить статус' для повторной проверки");
                    StatusText.Text = "Установка завершена, но программа не найдена";
                    ProgressBar.IsIndeterminate = false;
                    ProgressBar.Visibility = Visibility.Collapsed;
                }
            }
            else
            {
                AddLog("Ошибка при скачивании");
                StatusText.Text = "Ошибка установки";
                ProgressBar.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            AddLog($"Ошибка: {ex.Message}");
            StatusText.Text = "Ошибка";
            ProgressBar.Visibility = Visibility.Collapsed;
        }
        finally
        {
            _isInstalling = false;
        }
    }
    
    // Важно: этот метод НЕ async, НЕ void, НЕ await
    private void BtnUpdate_Click(object sender, RoutedEventArgs e)
    {
        BtnInstall_Click(sender, e);
    }
    
    private async void BtnLaunch_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrEmpty(_mainAppPath) || !File.Exists(_mainAppPath))
            {
                AddLog("Программа не найдена");
                MessageBox.Show("Программа не найдена. Нажмите 'Установить' сначала.",
                    "Ven4Tools Launcher", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            
            AddLog($"Запуск Ven4Tools из: {_mainAppPath}");
            
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = _mainAppPath,
                UseShellExecute = true,
                Verb = "runas"
            });
            
            if (process != null)
            {
                AddLog($"Процесс запущен (ID: {process.Id})");
                await Task.Delay(2000);
                
                if (!process.HasExited)
                {
                    AddLog("Программа работает");
                    await Task.Delay(500);
                    Close();
                }
                else
                {
                    AddLog("Процесс завершился сразу после запуска");
                    AddLog("Возможно, программа запустилась в фоновом режиме");
                }
            }
            else
            {
                AddLog("Не удалось запустить процесс");
            }
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            if (ex.NativeErrorCode == 1223)
            {
                AddLog("Пользователь отменил запрос прав администратора");
                MessageBox.Show("Для работы Ven4Tools требуются права администратора.\n" +
                    "Пожалуйста, разрешите запуск при следующем запросе.",
                    "Ven4Tools Launcher", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                AddLog($"Ошибка запуска: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            AddLog($"Ошибка: {ex.Message}");
        }
    }
    
    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        AddLog("Закрытие лаунчера");
        Close();
    }
    
    private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        AddLog("Обновление статуса...");
        _mainAppPath = FindVen4Tools();
        _currentUpdateInfo = await _updateService.CheckForUpdateAsync(_mainAppPath);
        await InitializeAsync();
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