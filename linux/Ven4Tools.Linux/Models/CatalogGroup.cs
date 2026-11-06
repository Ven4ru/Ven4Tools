namespace Ven4Tools.Linux.Models;

// Группа карточек каталога под заголовком категории («Браузеры  9»), как группы в клиенте Windows.
public sealed class CatalogGroup
{
    public required string Header { get; init; }
    public required List<AppItem> Items { get; init; }
    public string CountText => Items.Count.ToString();
}

// Кнопка-категория над карточками: название и число программ.
public sealed class CategoryChip
{
    // Ключ — русское название категории из каталога; пустой — «Все»
    public required string Key { get; init; }
    public required string Label { get; init; }
    public required int Count { get; init; }
}
