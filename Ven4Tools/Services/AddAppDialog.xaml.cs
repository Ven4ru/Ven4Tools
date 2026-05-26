using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Ven4Tools.Models;

namespace Ven4Tools.Services
{
    public partial class AddAppDialog : Window
    {
        public AppInfo? Result { get; private set; }
        private readonly ObservableCollection<WingetPackage> _searchResults = new();

        public AddAppDialog()
        {
            InitializeComponent();
            lstSearchResults.ItemsSource = _searchResults;
        }

        private void RbMode_Checked(object sender, RoutedEventArgs e)
        {
            if (panelWinget == null) return;
            bool isWinget = rbWinget.IsChecked == true;
            panelWinget.Visibility = isWinget ? Visibility.Visible : Visibility.Collapsed;
            panelUrl.Visibility    = isWinget ? Visibility.Collapsed : Visibility.Visible;
        }

        private void TxtSearchQuery_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) BtnSearchWinget_Click(sender, e);
        }

        private async void BtnSearchWinget_Click(object sender, RoutedEventArgs e)
        {
            string query = txtSearchQuery.Text.Trim();
            if (string.IsNullOrWhiteSpace(query))
            {
                MessageBox.Show("Введите название для поиска.", "Информация",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            btnSearchWinget.IsEnabled = false;
            pbSearchProgress.Visibility = Visibility.Visible;
            panelSearchResults.Visibility = Visibility.Collapsed;
            _searchResults.Clear();
            txtNoResults.Visibility = Visibility.Collapsed;

            try
            {
                var results = await Task.Run(() => SearchWingetPackages(query));
                foreach (var pkg in results)
                    _searchResults.Add(pkg);

                panelSearchResults.Visibility = Visibility.Visible;
                txtNoResults.Visibility = results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка поиска: {ex.Message}", "Ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                pbSearchProgress.Visibility = Visibility.Collapsed;
                btnSearchWinget.IsEnabled = true;
            }
        }

        private void LstSearchResults_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (lstSearchResults.SelectedItem is not WingetPackage pkg) return;
            txtWingetId.Text = pkg.Id;
            if (string.IsNullOrWhiteSpace(txtName.Text))
                txtName.Text = pkg.Name;
            txtValidateResult.Text = $"✅ {pkg.Name}  |  Версия: {pkg.Version}";
            txtValidateResult.Foreground = System.Windows.Media.Brushes.LightGreen;
        }

        private void TxtWingetId_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (btnValidateId == null) return;
            btnValidateId.IsEnabled = !string.IsNullOrWhiteSpace(txtWingetId.Text);
            if (lstSearchResults.SelectedItem == null)
            {
                txtValidateResult.Text = "";
                txtValidateResult.Foreground = System.Windows.Media.Brushes.Gray;
            }
        }

        private async void BtnValidateId_Click(object sender, RoutedEventArgs e)
        {
            string id = txtWingetId.Text.Trim();
            if (string.IsNullOrEmpty(id)) return;

            btnValidateId.IsEnabled = false;
            txtValidateResult.Text = "⏳ Проверяем...";
            txtValidateResult.Foreground = System.Windows.Media.Brushes.Gray;

            try
            {
                var (name, version) = await Task.Run(() => ValidateWingetId(id));
                if (name != null)
                {
                    txtValidateResult.Text = $"✅ {name}  |  Версия: {version ?? "—"}";
                    txtValidateResult.Foreground = System.Windows.Media.Brushes.LightGreen;
                    if (string.IsNullOrWhiteSpace(txtName.Text))
                        txtName.Text = name;
                }
                else
                {
                    txtValidateResult.Text = "⚠️ Не найдено в winget — можно добавить всё равно";
                    txtValidateResult.Foreground = System.Windows.Media.Brushes.Tomato;
                }
            }
            finally
            {
                btnValidateId.IsEnabled = true;
            }
        }

        private (string? Name, string? Version) ValidateWingetId(string id)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "winget",
                    Arguments = $"show --id {id} -e --source winget --accept-source-agreements",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8
                };

                using var process = Process.Start(psi);
                if (process == null) return (null, null);

                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0) return (null, null);

                string? name = null, version = null;
                foreach (var line in output.Split('\n'))
                {
                    var t = line.Trim();
                    // "Found Mozilla Firefox [Mozilla.Firefox]" or "Найдено ..."
                    if (name == null)
                    {
                        var m = Regex.Match(t, @"(?:Found|Найдено)\s+(.+?)\s+\[");
                        if (m.Success) { name = m.Groups[1].Value.Trim(); continue; }
                    }
                    if (version == null && (t.StartsWith("Version:") || t.StartsWith("Версия:")))
                    {
                        version = t.Split(':', 2).Last().Trim();
                    }
                }
                return (name, version);
            }
            catch { return (null, null); }
        }

        private List<WingetPackage> SearchWingetPackages(string query)
        {
            var results = new List<WingetPackage>();
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "winget",
                    Arguments = $"search --name \"{query}\" --source winget --accept-source-agreements",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8
                };

                using var process = Process.Start(psi);
                if (process == null) return results;

                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                bool headerPassed = false;
                foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!headerPassed)
                    {
                        if (line.Contains("--")) headerPassed = true;
                        continue;
                    }
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var parts = Regex.Split(line, @"\s{2,}");
                    if (parts.Length >= 2)
                    {
                        results.Add(new WingetPackage
                        {
                            Name    = parts[0].Trim(),
                            Id      = parts[1].Trim(),
                            Version = parts.Length > 2 ? parts[2].Trim() : "",
                            Source  = parts.Length > 3 ? parts[3].Trim() : "winget"
                        });
                    }
                }
            }
            catch (Exception ex) { Debug.WriteLine($"Ошибка поиска: {ex.Message}"); }
            return results;
        }

        private async void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            bool isWinget = rbWinget.IsChecked == true;

            if (string.IsNullOrWhiteSpace(txtName.Text))
            {
                MessageBox.Show("Введите название программы.", "Ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (isWinget && string.IsNullOrWhiteSpace(txtWingetId.Text))
            {
                MessageBox.Show("Введите Winget ID или выберите результат из поиска.", "Ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!isWinget && string.IsNullOrWhiteSpace(txtUrl.Text))
            {
                MessageBox.Show("Введите ссылку для скачивания.", "Ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var app = new AppInfo
            {
                Id          = "User." + Guid.NewGuid().ToString("N"),
                DisplayName = txtName.Text.Trim(),
                Category    = AppCategory.Пользовательские,
                IsUserAdded = true
            };

            string? wingetId = null;
            if (isWinget)
            {
                wingetId = txtWingetId.Text.Trim();
                app.AlternativeId = wingetId;
            }
            else
            {
                app.InstallerUrls.Add(txtUrl.Text.Trim());
            }

            Result = app;

            try
            {
                if (await new ConsentService().IsStatsAllowedAsync())
                    await StatsService.Instance.TrackUserAddAsync(
                        app.Id, wingetId, app.InstallerUrls.FirstOrDefault());
            }
            catch { }

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
