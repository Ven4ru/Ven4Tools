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
        private const string RedirectUri = ApiConfig.BaseUrl + "/api/yandex-callback.php";

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
                // yandex-callback.php выполняет обмен кода на токен НА СЕРВЕРЕ (используя
                // client_secret, которого нет в клиенте) и возвращает готовую сессию
                // вызовом window.opener.postMessage({token, user_id, name, email, is_admin}).
                // В WebView2 opener == null, поэтому инжектируем поддельный opener,
                // который пробрасывает весь объект ответа через WebView2 API.
                await webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(@"
                    (function() {
                        if (location.href.indexOf('yandex-callback.php') !== -1) {
                            window.opener = {
                                postMessage: function(data) {
                                    if (!data) return;
                                    if (window.chrome && window.chrome.webview) {
                                        window.chrome.webview.postMessage(JSON.stringify(data));
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

        /// <summary>
        /// Проверяет источник web-сообщения: доверяем только страницам OAuth Яндекса
        /// и нашему серверу (там живёт yandex-callback.php, который и шлёт сессию).
        /// Сообщения с любых других страниц игнорируются.
        /// </summary>
        private static bool IsTrustedOrigin(string? source)
        {
            if (string.IsNullOrWhiteSpace(source)) return false;
            if (!Uri.TryCreate(source, UriKind.Absolute, out var uri)) return false;
            if (uri.Scheme != Uri.UriSchemeHttps) return false;

            string host = uri.Host;
            string ownHost = new Uri(ApiConfig.BaseUrl).Host;
            return host.Equals(ownHost, StringComparison.OrdinalIgnoreCase)
                || host.Equals("oauth.yandex.ru", StringComparison.OrdinalIgnoreCase)
                || host.Equals("yandex.ru", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(".yandex.ru", StringComparison.OrdinalIgnoreCase);
        }

        private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                // Сообщения с недоверенных страниц не обрабатываем — защита от
                // подмены сессии произвольным сайтом, открытым внутри WebView
                if (!IsTrustedOrigin(e.Source))
                {
                    AppLogger.Write($"[YandexAuth] ⚠ Отклонено сообщение от недоверенного источника: {e.Source}");
                    return;
                }

                var json = e.TryGetWebMessageAsString();
                var data = JObject.Parse(json);

                // Если сервер вернул ошибку — показываем её
                var error = data["error"]?.ToString();
                if (!string.IsNullOrWhiteSpace(error))
                {
                    txtStatus.Text = $"Ошибка: {error}";
                    return;
                }

                // Доверяем только серверной сессии: token + user_id выдаёт сервер
                // после обмена OAuth-кода. Клиент больше НЕ передаёт личность сам —
                // это исключает подмену личности (отправку чужого yandex_id/email).
                var token = data["token"]?.ToString() ?? "";

                int userId = 0;
                var uidTok = data["user_id"];
                if (uidTok != null) int.TryParse(uidTok.ToString(), out userId);

                var name  = data["name"]?.ToString() ?? "";
                var email = data["email"]?.ToString() ?? "";

                bool isAdmin = false;
                var adminTok = data["is_admin"];
                if (adminTok != null)
                {
                    var s = adminTok.ToString();
                    isAdmin = s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase);
                }

                if (string.IsNullOrWhiteSpace(token) || userId <= 0)
                {
                    txtStatus.Text = "Ошибка: сервер не вернул сессию. Попробуйте снова.";
                    return;
                }

                txtStatus.Text = "Авторизация...";
                webView.IsEnabled = false;

                UserSession.Login(userId, name, email, isAdmin, token);
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                txtStatus.Text = $"Ошибка: {ex.Message}";
                webView.IsEnabled = true;
            }
        }
    }
}
