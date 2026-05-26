using System;
using System.Windows;
using Ven4Tools.Models;
using Ven4Tools.Services;

namespace Ven4Tools.Views
{
    public partial class LoginWindow : Window
    {
        private readonly AuthService _auth = new AuthService();
        private bool _isRegisterMode = false;

        public LoginWindow()
        {
            InitializeComponent();
            txtEmail.Focus();
        }

        private void BtnModeLogin_Click(object sender, RoutedEventArgs e) => SetMode(false);
        private void BtnModeRegister_Click(object sender, RoutedEventArgs e) => SetMode(true);

        private void SetMode(bool register)
        {
            _isRegisterMode = register;
            panelName.Visibility = register ? Visibility.Visible : Visibility.Collapsed;
            panelConfirm.Visibility = register ? Visibility.Visible : Visibility.Collapsed;
            txtTitle.Text = register ? "Создать аккаунт" : "Войти в аккаунт";
            btnSubmit.Content = register ? "Зарегистрироваться" : "Войти";

            btnModeLogin.Background = register
                ? (System.Windows.Media.Brush)FindResource("CardBackground")
                : (System.Windows.Media.Brush)FindResource("AccentColor");
            btnModeLogin.Foreground = register
                ? (System.Windows.Media.Brush)FindResource("TextPrimary")
                : System.Windows.Media.Brushes.White;

            btnModeRegister.Background = register
                ? (System.Windows.Media.Brush)FindResource("AccentColor")
                : (System.Windows.Media.Brush)FindResource("CardBackground");
            btnModeRegister.Foreground = register
                ? System.Windows.Media.Brushes.White
                : (System.Windows.Media.Brush)FindResource("TextPrimary");

            HideError();
        }

        private async void BtnSubmit_Click(object sender, RoutedEventArgs e)
        {
            HideError();
            btnSubmit.IsEnabled = false;
            btnSubmit.Content = "Подождите...";

            try
            {
                AuthResult result;

                if (_isRegisterMode)
                {
                    var name = txtName.Text.Trim();
                    var email = txtEmail.Text.Trim();
                    var password = txtPassword.Password;
                    var confirm = txtConfirm.Password;

                    if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
                    {
                        ShowError("Заполните все поля");
                        return;
                    }

                    if (password != confirm)
                    {
                        ShowError("Пароли не совпадают");
                        return;
                    }

                    result = await _auth.RegisterAsync(name, email, password);
                }
                else
                {
                    var email = txtEmail.Text.Trim();
                    var password = txtPassword.Password;

                    if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
                    {
                        ShowError("Введите email и пароль");
                        return;
                    }

                    result = await _auth.LoginAsync(email, password);
                }

                if (result.Success)
                {
                    UserSession.Login(result.UserId, result.Name, result.Email, result.IsAdmin);
                    DialogResult = true;
                    Close();
                }
                else
                {
                    ShowError(result.Error ?? "Неизвестная ошибка");
                }
            }
            finally
            {
                if (IsLoaded && IsVisible)
                {
                    btnSubmit.IsEnabled = true;
                    btnSubmit.Content = _isRegisterMode ? "Зарегистрироваться" : "Войти";
                }
            }
        }

        private void BtnYandex_Click(object sender, RoutedEventArgs e)
        {
            var win = new YandexAuthWindow { Owner = this };
            if (win.ShowDialog() == true)
            {
                DialogResult = true;
                Close();
            }
        }

        private void ShowError(string msg)
        {
            txtError.Text = "⚠ " + msg;
            txtError.Visibility = Visibility.Visible;
        }

        private void HideError()
        {
            txtError.Visibility = Visibility.Collapsed;
        }
    }
}
