using Ven4Tools.Linux.Services;

namespace Ven4Tools.Linux.Models;

// Карточка набора на вкладке «Наборы»: готовый набор из клиента Windows или свой.
public sealed class SetCard
{
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required List<AppItem> Available { get; init; }
    public required List<string> Missing { get; init; }
    // Пакет есть только на Flathub, а Flatpak в системе не включён
    public required List<string> FlathubOnly { get; init; }
    public UserPreset? UserPreset { get; init; }

    public bool IsUser => UserPreset is not null;
    public string AppsText => string.Join(", ", Available.Select(a => a.LinuxName));
    public bool HasMissing => Missing.Count > 0;
    public string MissingText =>
        (L.IsEnglish ? "Not available on Linux: " : "Нет для Linux, пропускается: ") + string.Join(", ", Missing);
    public bool HasFlathubOnly => FlathubOnly.Count > 0;
    public string FlathubOnlyText =>
        L.T("Есть на Flathub, после «Включить Flathub»: ") + string.Join(", ", FlathubOnly);
    public bool CanInstall => Available.Count > 0;
}
