using System;
using System.Text.Json.Serialization;

namespace Ven4Tools.Models
{
    public class App
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();

        public string Name { get; set; } = string.Empty;

        public string Category { get; set; } = "Другое";

        public string WingetId { get; set; } = string.Empty;

        public string DownloadUrl { get; set; } = string.Empty;

        public string Size { get; set; } = string.Empty;

        public string IconUrl { get; set; } = string.Empty;

        public string Profile { get; set; } = "full";

        public string ChocoId { get; set; } = string.Empty;

        public string? Sha256 { get; set; }

        // Переопределение флага тихой установки для конкретного установщика (например,
        // AutoHotkey v2 требует "/silent", а не общепринятый NSIS-флаг "/S"). Пусто —
        // используется дефолт AppInfo.SilentArgs ("/S").
        public string? SilentArgs { get; set; }

        [JsonIgnore]
        public bool IsSelected { get; set; }

        [JsonIgnore]
        public bool IsUnavailable { get; set; }

        [JsonIgnore]
        public string Status => IsUnavailable ? "❌ Недоступно" : "✅ Доступно";

        [JsonIgnore]
        public string Source { get; set; } = "online";
    }
}
