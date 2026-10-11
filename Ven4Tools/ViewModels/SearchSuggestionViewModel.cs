using System;

namespace Ven4Tools.ViewModels
{
    // Строка панели "не нашли в каталоге? поиск по источникам" — до перехода на
    // MVVM строилась императивно в code-behind вкладки (AddSuggestionRow), теперь
    // просто данные для DataTemplate.
    public sealed class SearchSuggestionViewModel
    {
        public string Name { get; }
        public string Hint { get; }
        public string SourceLabel { get; }
        public RelayCommand AddCommand { get; }

        public SearchSuggestionViewModel(string name, string hint, string sourceLabel, Action onAdd)
        {
            Name = name;
            Hint = hint;
            SourceLabel = sourceLabel;
            AddCommand = new RelayCommand(_ => onAdd());
        }
    }
}
