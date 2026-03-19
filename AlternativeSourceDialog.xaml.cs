using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Ven4Tools
{
    public class WingetPackage
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public string DisplayName => $"{Name} ({Id}) — {Version}";
    }

    public partial class AlternativeSourceDialog : Window
    {
        public WingetPackage? SelectedPackage { get; private set; }
        public string? CustomUrl { get; private set; }
        public bool UseWingetFirst { get; private set; }
        public bool UseUrlFirst { get; private set; }
        
        private readonly string appName;
        private bool hasWingetResults = false;

        public AlternativeSourceDialog(string appName)
        {
            InitializeComponent();
            this.appName = appName;
            this.Loaded += async (s, e) => await LoadWingetResults();
            
            // Изначально отключаем чекбокс winget
            chkPriorityWinget.IsEnabled = false;
        }

        private async Task LoadWingetResults()
        {
            pbSearch.Visibility = Visibility.Visible;
            cmbResults.IsEnabled = false;
            chkPriorityWinget.IsEnabled = false;

            try
            {
                var results = await Task.Run(() => SearchWinget(appName));
                
                if (results.Any())
                {
                    hasWingetResults = true;
                    cmbResults.ItemsSource = results;
                    cmbResults.SelectedIndex = 0;
                    cmbResults.IsEnabled = true;
                    chkPriorityWinget.IsEnabled = true;
                }
                else
                {
                    hasWingetResults = false;
                    cmbResults.IsEnabled = false;
                    chkPriorityWinget.IsEnabled = false;
                }
                
                pbSearch.Visibility = Visibility.Collapsed;
                CheckCanSave();
            }
            catch (Exception ex)
            {
                pbSearch.Visibility = Visibility.Collapsed;
                cmbResults.IsEnabled = false;
                chkPriorityWinget.IsEnabled = false;
                Debug.WriteLine($"Ошибка поиска: {ex.Message}");
            }
        }

        private List<WingetPackage> SearchWinget(string query)
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

        private void CmbResults_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            CheckCanSave();
        }

        private void TxtUrl_TextChanged(object sender, TextChangedEventArgs e)
        {
            CheckCanSave();
        }

        private void CheckCanSave()
        {
            btnOk.IsEnabled = (hasWingetResults && cmbResults.SelectedItem != null) || 
                              !string.IsNullOrWhiteSpace(txtUrl.Text);
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            // Сохраняем настройки приоритета только для выбранных источников
            if (hasWingetResults && cmbResults.SelectedItem != null)
            {
                SelectedPackage = (WingetPackage)cmbResults.SelectedItem;
                UseWingetFirst = chkPriorityWinget.IsChecked == true;
            }
            
            if (!string.IsNullOrWhiteSpace(txtUrl.Text))
            {
                string url = txtUrl.Text.Trim();
                if (url.StartsWith("http://") || url.StartsWith("https://"))
                {
                    CustomUrl = url;
                    UseUrlFirst = chkPriorityUrl.IsChecked == true;
                }
                else
                {
                    MessageBox.Show("Ссылка должна начинаться с http:// или https://", 
                        "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }
            
            if (SelectedPackage == null && CustomUrl == null)
            {
                MessageBox.Show("Выберите источник или укажите ссылку", 
                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
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