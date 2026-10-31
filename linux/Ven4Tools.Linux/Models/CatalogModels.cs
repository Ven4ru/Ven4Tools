using System.Text.Json.Serialization;

namespace Ven4Tools.Linux.Models;

// Только те поля master.json, которые нужны версии для Linux.
public sealed class MasterCatalog
{
    [JsonPropertyName("version")] public int Version { get; set; }
    [JsonPropertyName("lastUpdated")] public string LastUpdated { get; set; } = "";
    [JsonPropertyName("apps")] public List<CatalogApp> Apps { get; set; } = new();
}

public sealed class CatalogApp
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("category")] public string Category { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("descriptionEn")] public string DescriptionEn { get; set; } = "";
}

// Запись из linux-packages.json: каким пакетом программа ставится в Linux.
public sealed class LinuxPackage
{
    [JsonPropertyName("flatpak")] public string? Flatpak { get; set; }
    // Пакет на Flathub подтверждён автором программы; иначе это сборка сообщества
    [JsonPropertyName("flatpakVerified")] public bool FlatpakVerified { get; set; }
    [JsonPropertyName("snap")] public string? Snap { get; set; }
    [JsonPropertyName("snapPublisher")] public string? SnapPublisher { get; set; }
    [JsonPropertyName("snapClassic")] public bool SnapClassic { get; set; }
    // Не сама программа, а замена с тем же назначением (ShareX -> Flameshot)
    [JsonPropertyName("alternative")] public string? Alternative { get; set; }
}

public sealed class LinuxPackageMap
{
    [JsonPropertyName("apps")] public Dictionary<string, LinuxPackage> Apps { get; set; } = new();
}
