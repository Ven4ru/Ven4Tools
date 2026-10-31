using System.ComponentModel;
using System.Runtime.CompilerServices;
using Ven4Tools.Linux.Services;

namespace Ven4Tools.Linux.Models;

// Строка списка: программа из каталога + выбранный для неё пакет Linux.
public sealed class AppItem : INotifyPropertyChanged
{
    public required CatalogApp App { get; init; }
    public LinuxPackage? Package { get; init; }

    public string Id => App.Id;
    public string Name => App.Name;
    // Замена под Linux называется своим именем: «Flameshot (вместо Greenshot)»
    public string LinuxName => Package?.Alternative is { } alt ? $"{alt} ({L.T("вместо")} {Name})" : Name;
    public string Description =>
        L.IsEnglish && App.DescriptionEn.Length > 0 ? App.DescriptionEn : App.Description;
    // Категория из каталога — русская; в английском интерфейсе — те же слова, что у клиента Windows.
    public string Category => L.T(App.Category);

    // Источник выбирается при сборке списка (Flatpak, если он есть в системе)
    public BackendKind? Backend { get; init; }
    public string? PackageId => Backend switch
    {
        BackendKind.Flatpak => Package?.Flatpak,
        BackendKind.Snap => Package?.Snap,
        _ => null
    };

    public bool HasLinuxPackage => Backend is not null;

    public string SourceText => Backend switch
    {
        BackendKind.Flatpak => $"Flathub · {Package!.Flatpak}" +
                               (Package.FlatpakVerified ? "" : " · " + L.T("сборка сообщества")),
        BackendKind.Snap => $"Snap · {Package!.Snap} · {Package.SnapPublisher}" +
                            (Package.SnapClassic ? " (classic)" : ""),
        _ => Package is null ? L.T("Нет версии для Linux")
            : Package.Flatpak is not null ? L.T("Есть на Flathub - нажмите «Включить Flathub»")
            : L.T("Нужен Snap")
    };

    public string? AlternativeText =>
        Package?.Alternative is { } alt ? L.T("Под Linux вместо неё: ") + alt : null;

    public bool HasAlternative => AlternativeText is not null;

    private bool _isInstalled;
    public bool IsInstalled
    {
        get => _isInstalled;
        set { if (Set(ref _isInstalled, value)) { Raise(nameof(ActionText)); Raise(nameof(StatusText)); } }
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (!Set(ref _isBusy, value)) return;
            Raise(nameof(CanAct)); Raise(nameof(ActionText)); Raise(nameof(StatusText)); Raise(nameof(CanSelect));
        }
    }

    // Отметка для очереди установки
    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value && CanSelect);
    }

    private bool _isFavorite;
    public bool IsFavorite
    {
        get => _isFavorite;
        set { if (Set(ref _isFavorite, value)) Raise(nameof(FavoriteGlyph)); }
    }
    public string FavoriteGlyph => IsFavorite ? "★" : "☆";

    public bool CanSelect => HasLinuxPackage && !IsBusy;
    public bool CanAct => HasLinuxPackage && !IsBusy;
    public string ActionText => IsBusy ? L.T("Подождите…") : IsInstalled ? L.T("Удалить") : L.T("Установить");
    public string StatusText => IsBusy ? L.T("В очереди") : IsInstalled ? L.T("✓ Установлено") : "";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new(n));
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? n = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(n);
        return true;
    }
}
