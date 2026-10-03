namespace Ven4Tools.ViewModels
{
    /// <summary>
    /// Кнопка-категория над карточками каталога: название, число программ и признак
    /// «выбрана сейчас». <see cref="Key"/> равен null у кнопки «Все».
    /// </summary>
    public sealed class CategoryChipViewModel : ViewModelBase
    {
        public string? Key { get; }
        public string Label { get; }

        public CategoryChipViewModel(string? key, string label, int count)
        {
            Key = key;
            Label = label;
            _count = count;
        }

        private int _count;
        public int Count
        {
            get => _count;
            set => SetField(ref _count, value);
        }

        private bool _isActive;
        public bool IsActive
        {
            get => _isActive;
            set => SetField(ref _isActive, value);
        }

        public string AutomationId => Key == null ? "chipCategory_all" : $"chipCategory_{Key}";
    }
}
