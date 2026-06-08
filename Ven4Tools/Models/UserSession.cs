using System;
using System.IO;
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
                var data = new { UserId, Name, Email, IsAdmin, Token };
                File.WriteAllText(_sessionPath, JsonConvert.SerializeObject(data));
            }
            catch { }
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
                    Token = data.Token ?? "";
                }
            }
            catch { }
        }
    }
}
