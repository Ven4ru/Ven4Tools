using System;
using System.Collections.ObjectModel;
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
                var results = await WingetService.SearchAsync(query);
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
                var (name, version) = await WingetService.ValidateIdAsync(id);
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
            catch (TimeoutException)
            {
                // winget завис и был принудительно завершён по таймауту — показываем
                // это отдельно от «не найдено», чтобы пользователь понял, что можно повторить.
                txtValidateResult.Text = "⚠️ Winget не ответил вовремя — попробуйте ещё раз";
                txtValidateResult.Foreground = System.Windows.Media.Brushes.Tomato;
            }
            finally
            {
                btnValidateId.IsEnabled = true;
            }
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
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
            string sha256 = "";
            if (!isWinget)
            {
                string urlCandidate = txtUrl.Text.Trim();
                if (!urlCandidate.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show("Ссылка должна начинаться с https:// — незащищённые ссылки не поддерживаются.",
                        "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                sha256 = txtSha256.Text.Trim().ToLowerInvariant();
                if (sha256.Length > 0 && !HashHelper.HasExpectedHash(sha256))
                {
                    MessageBox.Show(
                        "SHA256 указан в неверном формате (нужно 64 hex-символа).\n" +
                        "Оставьте поле пустым, если хеш неизвестен.",
                        "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (sha256.Length == 0)
                {
                    var answer = MessageBox.Show(
                        "SHA256 не указан — без него установка по прямой ссылке будет " +
                        "пропущена как непроверенная (источник считается недоступным), " +
                        "пока хеш не будет добавлен через редактирование.\n\n" +
                        "Всё равно добавить приложение без хеша?",
                        "Нет SHA256", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (answer != MessageBoxResult.Yes) return;
                }
            }

            var app = new AppInfo
            {
                Id          = "User." + Guid.NewGuid().ToString("N"),
                DisplayName = txtName.Text.Trim(),
                Category    = AppCategory.Пользовательские,
                IsUserAdded = true
            };

            if (isWinget)
                app.AlternativeId = txtWingetId.Text.Trim();
            else
            {
                app.InstallerUrls.Add(txtUrl.Text.Trim());
                app.Sha256 = sha256;
            }

            Result = app;

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
