namespace Ven4Tools.ViewModels
{
    /// <summary>Строка списка порядка источников установки. Переставляется только
    /// порядком в ObservableCollection (.Move()) — свойства после создания не меняются,
    /// INotifyPropertyChanged не нужен.</summary>
    public sealed class SourceItem
    {
        public required string Id    { get; init; }
        public required string Label { get; init; }
    }
}
