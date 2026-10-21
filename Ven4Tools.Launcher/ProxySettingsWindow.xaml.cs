using System;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Launcher
{
    /// <summary>
    /// Л6 «Прокси»: режим «Системный» / «Вручную». Ручная настройка сохраняется только
    /// после успешной проверки ровно тех значений, что сейчас в полях; любая правка поля
    /// снимает отметку «проверено».
    /// </summary>
    public partial class ProxySettingsWindow : Window
    {
        private bool _loading;
        private bool _verified;
        private Uri? _verifiedAddress;
        private CancellationTokenSource? _testCts;

        public ProxySettingsWindow()
        {
            InitializeComponent();
            _loading = true;
            txtAddress.Text = LauncherProxy.ManualAddress.TrimEnd('/').Replace("http://", "", StringComparison.OrdinalIgnoreCase);
            txtUser.Text = LauncherProxy.ManualUserName;
            txtPasswordHint.Visibility = LauncherProxy.HasManualPassword ? Visibility.Visible : Visibility.Collapsed;
            rbManual.IsChecked = LauncherProxy.Mode == ProxyMode.Manual;
            rbSystem.IsChecked = LauncherProxy.Mode != ProxyMode.Manual;
            txtFlagOff.Visibility = LauncherProxy.ManualAllowed ? Visibility.Collapsed : Visibility.Visible;
            _loading = false;
            // Уже сохранённая ручная настройка однажды прошла проверку — повторно не требуем,
            // пока поля не тронуты.
            _verified = LauncherProxy.Mode == ProxyMode.Manual &&
                        LauncherProxy.TryParseAddress(LauncherProxy.ManualAddress, out _verifiedAddress);
            UpdateState();
            Closed += (_, _) => _testCts?.Cancel();
        }

        private void RbMode_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            UpdateState();
        }

        private void Field_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            _verified = false;
            // Сменили логин или начали вводить пароль — сохранённый пароль больше не подставляем.
            if (sender == pwdPassword || sender == txtUser) txtPasswordHint.Visibility = Visibility.Collapsed;
            UpdateState();
        }

        private void UpdateState()
        {
            bool manual = rbManual.IsChecked == true;
            pnlManual.IsEnabled = manual;
            btnSave.IsEnabled = !manual || _verified;
            btnSave.ToolTip = manual && !_verified ? "Сначала нажмите «Проверить» — сохранить можно только рабочий прокси." : null;
        }

        // Пароль для проверки: введённый, либо сохранённый, если поле пустое и подсказка видна.
        private string? CurrentPassword() =>
            pwdPassword.Password.Length > 0 || txtPasswordHint.Visibility != Visibility.Visible
                ? pwdPassword.Password
                : LauncherProxy.GetSavedPassword();

        private async void BtnTest_Click(object sender, RoutedEventArgs e)
        {
            if (!LauncherProxy.TryParseAddress(txtAddress.Text, out var address) || address == null)
            {
                ShowStatus("Адрес прокси не распознан. Укажите имя или IP и порт, например proxy.company.ru:3128.", "StatusDanger");
                return;
            }

            _testCts?.Cancel();
            _testCts = new CancellationTokenSource();
            btnTest.IsEnabled = false;
            ShowStatus("Проверяю соединение через прокси…", "TextSecondary");
            try
            {
                string? error = await LauncherHttp.TestProxyAsync(address, txtUser.Text.Trim(), CurrentPassword(), _testCts.Token);
                if (error == null)
                {
                    _verified = true;
                    _verifiedAddress = address;
                    ShowStatus("✅ Прокси работает. Нажмите «Сохранить», чтобы лаунчер ходил через него.", "StatusSuccess");
                }
                else
                {
                    _verified = false;
                    ShowStatus($"❌ {error} Настройка не сохранена.", "StatusDanger");
                }
                LauncherLog.Write($"[Прокси] Проверка ручного прокси: {error ?? "успешно"}");
            }
            catch (OperationCanceledException)
            {
                // Окно закрыто посреди проверки.
            }
            catch (Exception ex)
            {
                _verified = false;
                ShowStatus($"❌ Проверка не выполнена: {ex.Message}", "StatusDanger");
            }
            finally
            {
                btnTest.IsEnabled = true;
                UpdateState();
            }
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (rbManual.IsChecked == true)
            {
                if (!_verified || _verifiedAddress == null) { UpdateState(); return; }
                // null — оставить сохранённый пароль (поле пустое, подсказка «сохранён» видна).
                string? password = pwdPassword.Password.Length == 0 && txtPasswordHint.Visibility == Visibility.Visible
                    ? null
                    : pwdPassword.Password;
                LauncherProxy.SaveManual(_verifiedAddress, txtUser.Text, password);
            }
            else
            {
                LauncherProxy.UseSystem();
            }
            DialogResult = true;
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private void ShowStatus(string text, string brushKey)
        {
            txtStatus.Text = text;
            txtStatus.Foreground = (Brush)FindResource(brushKey);
            txtStatus.Visibility = Visibility.Visible;
        }
    }
}
