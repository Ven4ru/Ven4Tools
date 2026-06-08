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
        private const string BaseUrl = "https://www.ven4tools.ru/api/db.php";

        public Task<AuthResult> LoginAsync(string email, string password) =>
            PostAsync("login", new { email, password });

        public Task<AuthResult> RegisterAsync(string name, string email, string password) =>
            PostAsync("register", new { name, email, password });

        public Task<AuthResult> YandexLoginAsync(string yandexId, string name, string email) =>
            PostAsync("yandex_login", new { yandex_id = yandexId, name, email });

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
