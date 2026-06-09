using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace Ven4Tools.Models
{
    public static class UserSession
    {
        private static readonly string _sessionPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ven4Tools", "session.json");

        public static int UserId { get; private set; }
        public static string Name { get; private set; } = "";
        public static string Email { get; private set; } = "";
        public static bool IsAdmin { get; private set; }
        public static string Token { get; private set; } = "";
        public static bool IsLoggedIn => UserId > 0;

        public static event Action? Changed;

        static UserSession() => Load();

        public static void Login(int userId, string name, string email, bool isAdmin, string token = "")
        {
            UserId = userId;
            Name = name;
            Email = email;
            IsAdmin = isAdmin;
            Token = token;
            Save();
            Changed?.Invoke();
        }

        public static void Logout()
        {
            UserId = 0;
            Name = "";
            Email = "";
            IsAdmin = false;
            Token = "";
            Save();
            Changed?.Invoke();
        }

        private static void Save()
        {
            try
            {
                if (UserId == 0 || Services.ProfileService.Current.NoLocalStorage)
                {
                    if (File.Exists(_sessionPath)) File.Delete(_sessionPath);
                    return;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(_sessionPath)!);
                // Токен шифруем через DPAPI (привязка к текущему пользователю Windows),
                // чтобы он не лежал в session.json открытым текстом.
                var data = new { UserId, Name, Email, IsAdmin, Token = Protect(Token) };
                File.WriteAllText(_sessionPath, JsonConvert.SerializeObject(data));
            }
            catch (Exception ex) { Services.AppLogger.Write($"[UserSession] {ex.Message}"); }
        }

        private static void Load()
        {
            try
            {
                if (!File.Exists(_sessionPath)) return;
                var json = File.ReadAllText(_sessionPath);
                var data = JsonConvert.DeserializeAnonymousType(json,
                    new { UserId = 0, Name = "", Email = "", IsAdmin = false, Token = "" });
                if (data != null && data.UserId > 0)
                {
                    UserId = data.UserId;
                    Name = data.Name;
                    Email = data.Email;
                    IsAdmin = data.IsAdmin;
                    Token = Unprotect(data.Token ?? "");
                }
            }
            catch (Exception ex) { Services.AppLogger.Write($"[UserSession] {ex.Message}"); }
        }

        // ── Шифрование токена (DPAPI, CurrentUser) ──────────────────────────────────

        private static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            try
            {
                var bytes = ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(bytes);
            }
            catch (Exception ex)
            {
                Services.AppLogger.Write($"[UserSession] {ex.Message}");
                return plain; // в крайнем случае не теряем токен
            }
        }

        private static string Unprotect(string stored)
        {
            if (string.IsNullOrEmpty(stored)) return "";
            try
            {
                var bytes = ProtectedData.Unprotect(
                    Convert.FromBase64String(stored), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                // Обратная совместимость: старые сессии хранили токен открытым текстом.
                // Если расшифровка не удалась — считаем значение plaintext.
                return stored;
            }
        }
    }
}
