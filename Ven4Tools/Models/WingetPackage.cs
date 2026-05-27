namespace Ven4Tools.Models
{
    public class WingetPackage
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public string DisplayName => $"{Name} ({Id}) — {Version}";
    }
}
