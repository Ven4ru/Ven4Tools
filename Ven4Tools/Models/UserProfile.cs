namespace Ven4Tools.Models
{
    public class UserProfile
    {
        // Catalog
        public string CatalogMode { get; set; } = "full"; // "basic", "extended", "full"
        public bool HideInstalled { get; set; } = false;
        public string DefaultSort { get; set; } = "alpha"; // "alpha", "category", "popularity"
        public bool FreeOnly { get; set; } = false;

        // UI
        public string Theme { get; set; } = "dark";
        public string Language { get; set; } = "auto"; // "auto", "ru", "en"
        public bool CompactMode { get; set; } = false;
        public bool ShowDescriptions { get; set; } = true;

        // Install
        public bool SilentInstall { get; set; } = false;
        public bool AutoDependencies { get; set; } = true;
        public string DefaultInstallFolder { get; set; } = "";

        // Notifications
        public bool NotifyAppUpdates { get; set; } = true;
        public bool NotifyNewApps { get; set; } = false;

        // Sync & Privacy
        public bool SyncFavorites { get; set; } = true;
        public bool SaveInstallHistory { get; set; } = true;
        public bool AnonymousStats { get; set; } = false;
        public bool NoLocalStorage { get; set; } = false;

        // First-run
        public bool HasSelectedCategory { get; set; } = false;
    }
}
