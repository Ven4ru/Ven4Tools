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

// Отдельный каталог Linux-версии (Catalog/linux-catalog.json): свои программы, каждая проверена
// установкой на чистой системе (Tools/LinuxCatalog). Подписан тем же ключом, что master.json.
public sealed class LinuxCatalog
{
    // Признак типа: подписанный master.json нельзя выдать за каталог Linux и наоборот
    public const string KindValue = "ven4tools-linux-catalog";

    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("version")] public int Version { get; set; }
    [JsonPropertyName("lastUpdated")] public string LastUpdated { get; set; } = "";
    [JsonPropertyName("apps")] public List<LinuxCatalogApp> Apps { get; set; } = new();
}

public sealed class LinuxCatalogApp
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("nameEn")] public string? NameEn { get; set; }
    [JsonPropertyName("category")] public string Category { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("descriptionEn")] public string DescriptionEn { get; set; } = "";
    [JsonPropertyName("flatpak")] public string? Flatpak { get; set; }
    [JsonPropertyName("snap")] public string? Snap { get; set; }
    [JsonPropertyName("snapPublisher")] public string? SnapPublisher { get; set; }
    [JsonPropertyName("snapClassic")] public bool SnapClassic { get; set; }
    [JsonPropertyName("publisher")] public string Publisher { get; set; } = "";
    // Пакет выпускает сам автор программы (на Flathub — подтверждённый разработчик)
    [JsonPropertyName("official")] public bool Official { get; set; }
    // Какие программы каталога Windows она заменяет: наборы и профиль из Windows работают через это поле
    [JsonPropertyName("replaces")] public List<CatalogRef> Replaces { get; set; } = new();
    // ok — последняя проверка прошла; unavailable — не прошла, программа показывается приглушённой
    [JsonPropertyName("status")] public string Status { get; set; } = "ok";
    [JsonPropertyName("checked")] public CheckInfo? Checked { get; set; }
}

public sealed class CatalogRef
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
}

public sealed class CheckInfo
{
    [JsonPropertyName("date")] public string Date { get; set; } = "";
    [JsonPropertyName("result")] public string Result { get; set; } = "";
}
