using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Ven4Tools.Services
{
    public class AuthResult
    {
        public bool Success { get; set; }
        public string? Error { get; set; }
        public int UserId { get; set; }
        public string Name { get; set; } = "";
        public string Email { get; set; } = "";
        public bool IsAdmin { get; set; }
        public string Token { get; set; } = "";
    }

    public class AuthService
    {
        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        private const string BaseUrl = ApiConfig.DbApi;

        public Task<AuthResult> LoginAsync(string email, string password) =>
            PostAsync("login", new { email, password });

        public Task<AuthResult> RegisterAsync(string name, string email, string password) =>
            PostAsync("register", new { name, email, password });

        // Запрос восстановления пароля: сервер отправляет письмо со ссылкой на сброс.
        public Task<AuthResult> ForgotPasswordAsync(string email) =>
            PostAsync("forgot_password", new { email });

        // Вход через Яндекс выполняется полностью на сервере (yandex-callback.php):
        // сервер обменивает OAuth-код на токен и сам создаёт сессию. Клиент получает
        // готовый токен и не передаёт личность вручную (см. YandexAuthWindow).

        // Серверный выход из аккаунта: помечает токен недействительным на сервере.
        // Локальная сессия очищается отдельно (UserSession.Logout) — этот вызов лишь
        // уведомляет сервер и не влияет на то, что пользователь уже вышел локально.
        public async Task LogoutAsync(string token)
        {
            if (string.IsNullOrEmpty(token)) return;
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}?action=logout");
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                await _http.SendAsync(request);
            }
            catch (Exception ex) { AppLogger.Write($"[AuthService] logout: {ex.Message}"); }
        }

        // Установка или смена пароля. Сервер (db.php, action set_password) ждёт поле password.
        // Для OAuth-аккаунтов (например, Яндекс) старый пароль не требуется.
        public async Task<AuthResult> SetPasswordAsync(string token, string newPassword, string? oldPassword = null)
        {
            try
            {
                object payload = new { password = newPassword };

                var json = JsonConvert.SerializeObject(payload);
                var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}?action=set_password");
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _http.SendAsync(request);
                var body = await response.Content.ReadAsStringAsync();
                var data = JObject.Parse(body);

                if (data["error"] != null)
                    return new AuthResult { Error = data["error"]!.ToString() };

                return new AuthResult { Success = true };
            }
            catch (TaskCanceledException)
            {
                return new AuthResult { Error = "Превышено время ожидания." };
            }
            catch (Exception ex)
            {
                return new AuthResult { Error = $"Ошибка: {ex.Message}" };
            }
        }

        private async Task<AuthResult> PostAsync(string action, object payload)
        {
            try
            {
                var json = JsonConvert.SerializeObject(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await _http.PostAsync($"{BaseUrl}?action={action}", content);
                var body = await response.Content.ReadAsStringAsync();

                var data = JObject.Parse(body);

                if (data["error"] != null)
                    return new AuthResult { Error = data["error"]!.ToString() };

                return new AuthResult
                {
                    Success = true,
                    UserId = data["user_id"]?.Value<int>() ?? 0,
                    Name = data["name"]?.ToString() ?? "",
                    Email = data["email"]?.ToString() ?? "",
                    IsAdmin = data["is_admin"]?.Value<bool>() ?? false,
                    Token = data["token"]?.ToString() ?? ""
                };
            }
            catch (TaskCanceledException)
            {
                return new AuthResult { Error = "Превышено время ожидания. Проверьте подключение к интернету." };
            }
            catch (Exception ex)
            {
                return new AuthResult { Error = $"Ошибка подключения: {ex.Message}" };
            }
        }
    }
}
