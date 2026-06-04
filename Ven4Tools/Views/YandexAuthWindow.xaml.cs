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
                        if (location.href.indexOf('yandex-callback.php') !== -1) {
                            window.opener = {
                                postMessage: function(data) {
                                    if (!data || !data.user_id) return;
                                    if (window.chrome && window.chrome.webview) {
                                        window.chrome.webview.postMessage(JSON.stringify({
                                            yandex_id: String(data.user_id),
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
                var json = e.TryGetWebMessageAsString();
                var data = JObject.Parse(json);

                var yandexId = data["yandex_id"]?.ToString() ?? "";
                var name     = data["name"]?.ToString() ?? "";
                var email    = data["email"]?.ToString() ?? "";

                if (string.IsNullOrWhiteSpace(yandexId))
                {
                    txtStatus.Text = "Ошибка: не получены данные от Яндекса. Попробуйте снова.";
                    return;
                }

                txtStatus.Text = "Авторизация...";
                webView.IsEnabled = false;

                var result = await _auth.YandexLoginAsync(yandexId, name, email);

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
