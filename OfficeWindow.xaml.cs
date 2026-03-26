using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;

namespace Ven4Tools
{
    public partial class OfficeWindow : Window
    {
        private readonly MainWindow mainWindow;
        private HttpClient? httpClient;
        private CancellationTokenSource? cancellationTokenSource;
        private bool isCancelled = false;
        private string? originalCountryCode;
        
        private readonly Dictionary<string, string> officeVersions;
        private readonly string[] officeLanguages;
        
        private readonly Dictionary<string, string> officeDirectLinks = new()
        {
            { "O365ProPlusRetail", "https://c2rsetup.officeapps.live.com/c2r/download.aspx?ProductreleaseID=O365ProPlusRetail&platform=x64&language={0}&version=O16GA" },
            { "ProPlus2024Retail", "https://c2rsetup.officeapps.live.com/c2r/download.aspx?ProductreleaseID=ProPlus2024Retail&platform=x64&language={0}&version=O16GA" },
            { "Professional2021Retail", "https://c2rsetup.officeapps.live.com/c2r/download.aspx?ProductreleaseID=Professional2021Retail&platform=x64&language={0}&version=O16GA" },
            { "Professional2019Retail", "https://c2rsetup.officeapps.live.com/c2r/download.aspx?ProductreleaseID=Professional2019Retail&platform=x64&language={0}&version=O16GA" },
            { "ProPlusRetail", "https://c2rsetup.officeapps.live.com/c2r/download.aspx?ProductreleaseID=ProPlusRetail&platform=x64&language={0}&version=O16GA" }
        };

        public OfficeWindow(Dictionary<string, string> versions, string[] languages, MainWindow main)
        {
            InitializeComponent();
            officeVersions = versions;
            officeLanguages = languages;
            mainWindow = main;
            
            SaveOriginalCountryCode();

            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                UseCookies = true,
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
            };
            
            httpClient = new HttpClient(handler);
            httpClient.DefaultRequestHeaders.Clear();
            httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36 Edg/120.0.0.0");
            httpClient.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8");
            httpClient.DefaultRequestHeaders.Add("Accept-Language", "ru-RU,ru;q=0.9,en;q=0.8");
            httpClient.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate, br");
            httpClient.DefaultRequestHeaders.Add("Connection", "keep-alive");
            httpClient.DefaultRequestHeaders.Add("Upgrade-Insecure-Requests", "1");

            cmbOfficeVersion.ItemsSource = officeVersions;
            cmbOfficeVersion.SelectedIndex = 0;
            cmbOfficeLanguage.ItemsSource = officeLanguages;
            cmbOfficeLanguage.SelectedIndex = 0;
            
            this.Closing += OfficeWindow_Closing!;
        }

        private void SaveOriginalCountryCode()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Office\16.0\Common\ExperimentConfigs\Ecs"))
                {
                    originalCountryCode = key?.GetValue("CountryCode")?.ToString();
                }
            }
            catch { originalCountryCode = null; }
        }

        private bool SetCountryCode(string countryCode)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Office\16.0\Common\ExperimentConfigs\Ecs"))
                {
                    if (key != null)
                    {
                        key.SetValue("CountryCode", $"std::wstring|{countryCode}", RegistryValueKind.String);
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                mainWindow.LogMessage($"⚠️ Ошибка при установке CountryCode: {ex.Message}");
            }
            return false;
        }

        private bool RestoreOriginalCountryCode()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Office\16.0\Common\ExperimentConfigs\Ecs", true))
                {
                    if (key != null)
                    {
                        if (originalCountryCode != null)
                            key.SetValue("CountryCode", originalCountryCode, RegistryValueKind.String);
                        else
                            key.DeleteValue("CountryCode", false);
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                mainWindow.LogMessage($"⚠️ Ошибка при восстановлении CountryCode: {ex.Message}");
            }
            return false;
        }

        private void OfficeWindow_Closing(object sender, CancelEventArgs e)
        {
            if (cancellationTokenSource != null && !cancellationTokenSource.IsCancellationRequested && !isCancelled)
            {
                var result = MessageBox.Show(
                    "Установка ещё не завершена. Всё равно закрыть окно?",
                    "Подтверждение",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                
                if (result == MessageBoxResult.No)
                {
                    e.Cancel = true;
                    return;
                }
                RestoreOriginalCountryCode();
                CancelDownload();
            }
            else RestoreOriginalCountryCode();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            RestoreOriginalCountryCode();
            CancelDownload();
            mainWindow.LogMessage("❌ Установка Office отменена пользователем");
            DialogResult = false;
            Close();
        }

        private void CancelDownload()
        {
            if (cancellationTokenSource != null && !cancellationTokenSource.IsCancellationRequested)
            {
                isCancelled = true;
                cancellationTokenSource.Cancel();
            }
        }

        private async void BtnInstall_Click(object sender, RoutedEventArgs e)
        {
            if (cmbOfficeVersion.SelectedItem == null || cmbOfficeLanguage.SelectedItem == null) 
                return;

            var version = (KeyValuePair<string, string>)cmbOfficeVersion.SelectedItem;
            string productId = version.Value;
            string lang = cmbOfficeLanguage.SelectedItem.ToString()!;
            string displayName = version.Key;

            btnInstall.IsEnabled = false;
            btnCancel.IsEnabled = true;
            isCancelled = false;
            cancellationTokenSource = new CancellationTokenSource();
            var token = cancellationTokenSource.Token;

            mainWindow.LogMessage($"\n📦 Установка {displayName} ({lang})...");

            try
            {
                mainWindow.LogMessage($"🌎 Установка CountryCode = US для обхода блокировок...");
                SetCountryCode("US");
                await Task.Delay(500, token);

                string downloadUrl = string.Format(officeDirectLinks[productId], lang);
                string tempFile = Path.Combine(Path.GetTempPath(), $"OfficeSetup_{Guid.NewGuid():N}.exe");
                
                mainWindow.LogMessage($"📥 Скачивание...");
                
                using (var response = await httpClient!.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, token))
                {
                    response.EnsureSuccessStatusCode();
                    
                    using (var contentStream = await response.Content.ReadAsStreamAsync())
                    using (var fileStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        await contentStream.CopyToAsync(fileStream, token);
                    }
                }

                if (token.IsCancellationRequested || isCancelled)
                {
                    try { File.Delete(tempFile); } catch { }
                    mainWindow.LogMessage($"⏹️ Скачивание прервано");
                    return;
                }

                var fileInfo = new FileInfo(tempFile);
                mainWindow.LogMessage($"✅ Скачано: {fileInfo.Length / 1024 / 1024:F1} MB");
                mainWindow.LogMessage($"🚀 Запуск установки...");

                Process.Start(new ProcessStartInfo
                {
                    FileName = tempFile,
                    UseShellExecute = true,
                    Verb = "runas"
                });

                mainWindow.LogMessage($"✅ Установщик запущен!");

                if (chkSaveInstaller.IsChecked != true)
                {
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(5000);
                        try { File.Delete(tempFile); } catch { }
                    });
                }

                MessageBox.Show(
                    $"Установщик {displayName} запущен!\n\n" +
                    $"Файл сохранён: {tempFile}\n" +
                    $"Размер: {fileInfo.Length / 1024 / 1024:F1} MB",
                    "Установка запущена",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                DialogResult = true;
                Close();
            }
            catch (OperationCanceledException)
            {
                mainWindow.LogMessage($"⏹️ Скачивание отменено");
            }
            catch (Exception ex)
            {
                if (!isCancelled)
                {
                    mainWindow.LogMessage($"❌ Ошибка: {ex.Message}");
                    MessageBox.Show($"Ошибка: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            finally
            {
                RestoreOriginalCountryCode();
                cancellationTokenSource?.Dispose();
                cancellationTokenSource = null;
            }
        }
    }
}