using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Ven4Tools.Models;
using Ven4Tools.Services;

namespace Ven4Tools.Services
{
    public partial class AddAppDialog : Window
    {
        public AppInfo? Result { get; private set; }
        private ObservableCollection<WingetPackage> searchResults = new();

        public AddAppDialog()
        {
            InitializeComponent();
            lstSearchResults.ItemsSource = searchResults;
        }

        private async void BtnSearchWinget_Click(object sender, RoutedEventArgs e)
        {
            string query = txtName.Text.Trim();
            if (string.IsNullOrWhiteSpace(query))
            {
                MessageBox.Show("Введите название программы для поиска.", "Информация", 
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            pbSearchProgress.Visibility = Visibility.Visible;
            panelSearchResults.Visibility = Visibility.Collapsed;
            searchResults.Clear();
            txtNoResults.Visibility = Visibility.Collapsed;

            try
            {
                var results = await Task.Run(() => SearchWingetPackages(query));
                
                foreach (var pkg in results)
                {
                    searchResults.Add(pkg);
                }

                if (searchResults.Count > 0)
                {
                    panelSearchResults.Visibility = Visibility.Visible;
                }
                else
                {
                    txtNoResults.Visibility = Visibility.Visible;
                    panelSearchResults.Visibility = Visibility.Visible;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка при поиске в winget: {ex.Message}", "Ошибка", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                pbSearchProgress.Visibility = Visibility.Collapsed;
            }
        }

        private List<WingetPackage> SearchWingetPackages(string query)
        {
            var results = new List<WingetPackage>();

            try
            {
                string args = $"search --name \"{query}\" --source winget --accept-source-agreements";
                
                var psi = new ProcessStartInfo
                {
                    FileName = "winget.exe",
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8
                };

                using (var process = Process.Start(psi))
                {
                    if (process == null) return results;

                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();

                    var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    bool headerPassed = false;
                    foreach (var line in lines)
                    {
                        if (!headerPassed && line.Contains("--"))
                        {
                            headerPassed = true;
                            continue;
                        }
                        if (!headerPassed || line.StartsWith("Имя") || string.IsNullOrWhiteSpace(line))
                            continue;

                        var parts = Regex.Split(line, @"\s{2,}");
                        if (parts.Length >= 3)
                        {
                            var pkg = new WingetPackage
                            {
                                Name = parts[0].Trim(),
                                Id = parts[1].Trim(),
                                Version = parts[2].Trim(),
                                Source = parts.Length > 3 ? parts[3].Trim() : "winget"
                            };
                            results.Add(pkg);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка поиска: {ex.Message}");
            }

            return results;
        }

        private async void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtName.Text))
            {
                MessageBox.Show("Введите название программы", "Ошибка", 
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string wingetIdValue = txtWingetId.Text.Trim();
            string? selectedWingetId = null;
            
            if (lstSearchResults.SelectedItem is WingetPackage selected)
            {
                wingetIdValue = selected.Id;
                selectedWingetId = selected.Id;
            }

            var app = new AppInfo
            {
                Id = string.IsNullOrWhiteSpace(wingetIdValue) 
                    ? "User." + Guid.NewGuid().ToString("N") 
                    : wingetIdValue,
                DisplayName = txtName.Text,
                Category = AppCategory.Пользовательские,
                IsUserAdded = true
            };

            if (!string.IsNullOrWhiteSpace(txtUrl.Text))
            {
                app.InstallerUrls.Add(txtUrl.Text);
            }

            Result = app;
            
            // Отправка статистики
            try
            {
                var consentService = new ConsentService();
                var allowStats = await consentService.IsStatsAllowedAsync();
                
                if (allowStats)
                {
                    var statsService = new StatsService();
                    await statsService.TrackUserAddAsync(
                        app.Id,
                        selectedWingetId,
                        txtUrl.Text.Trim()
                    );
                    Debug.WriteLine($"Статистика отправлена для {app.DisplayName}");
                }
                else
                {
                    Debug.WriteLine("Статистика не отправляется (пользователь отказался)");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка статистики: {ex.Message}");
            }
            
            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}