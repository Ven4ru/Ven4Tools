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

    // Из каталога Linux: какие программы Windows она заменяет (PeaZip — 7-Zip)
    public IReadOnlyList<CatalogRef> Replaces { get; init; } = Array.Empty<CatalogRef>();
    // Последняя проверка установкой не прошла — программа приглушена, поставить нельзя
    public bool Unavailable { get; init; }
    public string? CheckedDate { get; init; }
    // Snap-пакет от самого автора программы (для Flatpak — Package.FlatpakVerified)
    public bool SnapOfficial { get; init; }

    // Под какими id программа известна каталогу Windows: свой и тех, кого заменяет
    public IEnumerable<string> KnownIds => Replaces.Select(r => r.Id).Prepend(Id);

    // Показывается без переключателя «Недоступные»: есть пакет Linux и последняя проверка прошла
    public bool ShownByDefault => Package is not null && !Unavailable;

    private string UnavailableText => L.T("Временно недоступна") +
        (CheckedDate is { Length: >= 10 } d ? $" ({L.T("проверка")} {d[8..10]}.{d[5..7]})" : "");
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

    private string BuildKind(bool official) => official ? L.T("официальная сборка") : L.T("сборка сообщества");

    public string SourceText => Unavailable
        ? UnavailableText + (Package?.Flatpak is { } f ? " · " + f : "")
        : Backend switch
        {
            BackendKind.Flatpak => $"Flathub · {Package!.Flatpak} · " + BuildKind(Package.FlatpakVerified),
            BackendKind.Snap => $"Snap · {Package!.Snap} · {Package.SnapPublisher} · " + BuildKind(SnapOfficial) +
                                (Package.SnapClassic ? " (classic)" : ""),
            _ => Package is null ? L.T("Нет версии для Linux")
                : Package.Flatpak is not null ? L.T("Есть на Flathub - нажмите «Включить Flathub»")
                : L.T("Нужен Snap")
        };

    // Строка под названием на карточке: откуда ставится, коротко. Полный идентификатор — в подсказке (SourceText)
    // На узкой карточке — коротко: «Flathub · авторская», «Недоступна · 05.11»
    public string MetaText => Unavailable
        ? L.T("Недоступна") + (CheckedDate is { Length: >= 10 } d ? $" · {d[8..10]}.{d[5..7]}" : "")
        : Backend switch
        {
            BackendKind.Flatpak => "Flathub · " + (Package!.FlatpakVerified ? L.T("авторская") : L.T("сообщество")),
            BackendKind.Snap => "Snap · " + (SnapOfficial ? L.T("авторская") : L.T("сообщество")) +
                                (Package!.SnapClassic ? " · classic" : ""),
            _ => Package is null ? L.T("Нет версии для Linux")
                : Package.Flatpak is not null ? L.T("Есть на Flathub") : L.T("Нужен Snap")
        };

    // Буква на месте значка, как у карточки клиента Windows, пока значка нет
    public string Monogram => Name.Length > 0 ? char.ToUpperInvariant(Name[0]).ToString() : "?";

    public string? AlternativeText =>
        Package?.Alternative is { } alt ? L.T("Под Linux вместо неё: ") + alt
        : Replaces.Count > 0 ? L.T("Вместо: ") + string.Join(", ", Replaces.Select(r => r.Name))
        : null;

    public bool HasAlternative => AlternativeText is not null;

    private bool _isInstalled;
    public bool IsInstalled
    {
        get => _isInstalled;
        set
        {
            if (!Set(ref _isInstalled, value)) return;
            Raise(nameof(ActionText)); Raise(nameof(StatusText)); Raise(nameof(IsNotInstalled)); Raise(nameof(HasStatus));
            Raise(nameof(IsAvailable));
        }
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (!Set(ref _isBusy, value)) return;
            Raise(nameof(CanAct)); Raise(nameof(ActionText)); Raise(nameof(StatusText)); Raise(nameof(CanSelect));
            Raise(nameof(SelectLabel)); Raise(nameof(HasStatus));
        }
    }

    // Отметка для очереди установки («Добавить» в «Ваш набор»)
    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { if (Set(ref _isSelected, value && CanSelect)) Raise(nameof(SelectLabel)); }
    }

    public bool IsNotInstalled => !IsInstalled;
    // Можно поставить и ещё не стоит — зелёное название на карточке
    public bool IsAvailable => HasLinuxPackage && !IsInstalled;
    public bool HasStatus => StatusText.Length > 0;

    // Подпись кнопки во всю ширину карточки — как «Добавить» у клиента Windows
    public string SelectLabel =>
        IsBusy ? L.T("Подождите…")
        : Unavailable ? L.T("Временно недоступна")
        : !HasLinuxPackage ? (Package?.Flatpak is not null ? L.T("Нужен Flathub") : L.T("Нет для Linux"))
        : IsSelected ? L.T("✓ В наборе") : L.T("Добавить");

    private bool _isFavorite;
    public bool IsFavorite
    {
        get => _isFavorite;
        set { if (Set(ref _isFavorite, value)) Raise(nameof(FavoriteIcon)); }
    }
    public Avalonia.Media.Geometry FavoriteIcon => IsFavorite ? Views.Icons.StarFilled : Views.Icons.StarOutline;

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
