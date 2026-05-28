using System;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json.Linq;
using Ven4Tools.Models;
using Ven4Tools.Services;

namespace Ven4Tools.Views
{
    public partial class YandexAuthWindow : Window
    {
        private const string ClientId = "90c3e4719791495cac5594768e41b248";
        private const string RedirectUri = "https://ven4tools.ru/api/yandex-callback.php";
        private readonly AuthService _auth = new AuthService();

        public YandexAuthWindow()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                await webView.EnsureCoreWebView2Async();

                // Перехватываем window.opener.postMessage на странице callback.
                // Callback.php вызывает window.opener.postMessage({user_id, name, email}, ...) —
                // в WebView2 opener == null, поэтому инжектируем поддельный opener,
                // который пробрасывает данные через WebView2 API.
                await webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(@"
                    (function() {
                        if (location.hostname.endsWith('ven4tools.ru') && location.pathname === '/api/yandex-callback.php') {
                            window.opener = {
                                postMessage: function(data) {
                                    if (window.chrome && window.chrome.webview) {
                                        window.chrome.webview.postMessage(JSON.stringify({
                                            yandex_id: String(data.user_id || ''),
                                            name: String(data.name || ''),
                                            email: String(data.email || '')
                                        }));
                                    }
                                }
                            };
                        }
                    })();
                ");

                webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

                var url = $"https://oauth.yandex.ru/authorize?response_type=code" +
                          $"&client_id={ClientId}" +
                          $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
                          $"&state=desktop";

                webView.Source = new Uri(url);
                txtStatus.Text = "Войдите в аккаунт Яндекс";
            }
            catch (Exception ex)
            {
                txtStatus.Text = $"Ошибка инициализации: {ex.Message}";
            }
        }

        private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                txtStatus.Text = "Авторизация...";
                webView.IsEnabled = false;

                var json = e.TryGetWebMessageAsString();
                var data = JObject.Parse(json);

                var result = await _auth.YandexLoginAsync(
                    data["yandex_id"]!.ToString(),
                    data["name"]!.ToString(),
                    data["email"]?.ToString() ?? ""
                );

                if (result.Success)
                {
                    UserSession.Login(result.UserId, result.Name, result.Email, result.IsAdmin);
                    DialogResult = true;
                    Close();
                }
                else
                {
                    txtStatus.Text = $"Ошибка: {result.Error}";
                    webView.IsEnabled = true;
                }
            }
            catch (Exception ex)
            {
                txtStatus.Text = $"Ошибка: {ex.Message}";
                webView.IsEnabled = true;
            }
        }
    }
}
