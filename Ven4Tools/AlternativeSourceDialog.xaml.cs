using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Ven4Tools.Models;
using Ven4Tools.Services;

namespace Ven4Tools
{
    public partial class AlternativeSourceDialog : Window
    {
        public WingetPackage? SelectedPackage { get; private set; }
        public string? CustomUrl { get; private set; }
        public bool UseWingetFirst { get; private set; }
        public bool UseUrlFirst { get; private set; }

        private readonly string _appName;
        private bool _hasWingetResults = false;

        public AlternativeSourceDialog(string appName)
        {
            InitializeComponent();
            _appName = appName;
            this.Loaded += async (s, e) => await LoadWingetResultsAsync();
            chkPriorityWinget.IsEnabled = false;
        }

        private async Task LoadWingetResultsAsync()
        {
            pbSearch.Visibility = Visibility.Visible;
            cmbResults.IsEnabled = false;
            chkPriorityWinget.IsEnabled = false;

            try
            {
                var results = await WingetService.SearchAsync(_appName);

                if (results.Count > 0)
                {
                    _hasWingetResults = true;
                    cmbResults.ItemsSource = results;
                    cmbResults.SelectedIndex = 0;
                    cmbResults.IsEnabled = true;
                    chkPriorityWinget.IsEnabled = true;
                }
                else
                {
                    _hasWingetResults = false;
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
                System.Diagnostics.Debug.WriteLine($"Search error: {ex.Message}");
            }
        }

        private void TxtManualId_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtManualId.Text)) return;

            var manualPackage = new WingetPackage
            {
                Name = "Ручной ввод",
                Id = txtManualId.Text.Trim(),
                Version = "manual",
                Source = "manual"
            };

            var list = cmbResults.ItemsSource as List<WingetPackage>;
            if (list != null && !list.Exists(p => p.Id == manualPackage.Id))
            {
                list.Insert(0, manualPackage);
                cmbResults.SelectedItem = manualPackage;
            }
            else
            {
                cmbResults.SelectedItem = cmbResults.Items.Count > 0 ? cmbResults.Items[0] : null;
            }

            _hasWingetResults = true;
            btnOk.IsEnabled = true;
        }

        private void CmbResults_SelectionChanged(object sender, SelectionChangedEventArgs e) => CheckCanSave();

        private void TxtUrl_TextChanged(object sender, TextChangedEventArgs e) => CheckCanSave();

        private void CheckCanSave()
        {
            btnOk.IsEnabled = (_hasWingetResults && cmbResults.SelectedItem != null) ||
                              !string.IsNullOrWhiteSpace(txtUrl.Text);
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            if (_hasWingetResults && cmbResults.SelectedItem is WingetPackage selected)
            {
                string message = $"Вы выбрали:\n\n" +
                                 $"Название: {selected.Name}\n" +
                                 $"ID: {selected.Id}\n" +
                                 $"Версия: {selected.Version}\n\n" +
                                 $"Будет сохранён ID: {selected.Id}";

                var result = MessageBox.Show(message, "Подтверждение выбора",
                    MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result == MessageBoxResult.No) return;

                SelectedPackage = selected;
                UseWingetFirst = chkPriorityWinget.IsChecked == true;
            }

            if (!string.IsNullOrWhiteSpace(txtUrl.Text))
            {
                string url = txtUrl.Text.Trim();
                if (!url.StartsWith("http://") && !url.StartsWith("https://"))
                {
                    MessageBox.Show("Ссылка должна начинаться с http:// или https://",
                        "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                CustomUrl = url;
                UseUrlFirst = chkPriorityUrl.IsChecked == true;
            }

            if (SelectedPackage == null && CustomUrl == null)
            {
                MessageBox.Show("Выберите источник или укажите ссылку",
                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!string.IsNullOrWhiteSpace(txtManualId?.Text) && SelectedPackage == null)
            {
                SelectedPackage = new WingetPackage
                {
                    Name = "Ручной ввод",
                    Id = txtManualId.Text.Trim(),
                    Version = "manual",
                    Source = "manual"
                };
                UseWingetFirst = chkPriorityWinget.IsChecked == true;
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
