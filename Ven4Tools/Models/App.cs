using System;

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

        public string Description { get; set; } = string.Empty;

        public string Version { get; set; } = string.Empty;

        public string Profile { get; set; } = "full";

        public string ChocoId { get; set; } = string.Empty;

        public string? Sha256 { get; set; }

        // Переопределение флага тихой установки для конкретного установщика (например,
        // AutoHotkey v2 требует "/silent", а не общепринятый NSIS-флаг "/S"). Пусто —
        // используется дефолт AppInfo.SilentArgs ("/S").
        public string? SilentArgs { get; set; }

        // Полей IsSelected/IsUnavailable/Status/Source здесь больше нет: это остатки
        // code-behind вкладки «Каталог», удалённого при переходе на MVVM (2026-07-13).
        // Состояние строки каталога живёт в AppRowViewModel, источник загрузки
        // каталога — в MasterCatalog.Source; на удалённые свойства не ссылался
        // ни один файл, а [JsonIgnore] на них создавал ложное впечатление, что
        // модель что-то отслеживает.
    }
}
