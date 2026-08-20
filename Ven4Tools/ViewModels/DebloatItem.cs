using System.ComponentModel;

namespace Ven4Tools.ViewModels
{
    /// <summary>
    /// Строка списка на вкладке «Очистка»: описание твика плюс состояние галочки.
    /// Вынесено из code-behind вкладки — это модель строки, а не логика окна.
    /// </summary>
    public class DebloatItem : INotifyPropertyChanged
    {
        public string Name        { get; }
        public string Id          { get; }
        public string Category    { get; } // "app", "privacy", "service"
        public string Risk        { get; } // "safe", "moderate", "caution"
        public string Description { get; }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(); }
        }

        public string RiskLabel => Risk switch
        {
            "safe"     => "Безопасно",
            "moderate" => "Умеренно",
            "caution"  => "Осторожно",
            _          => Risk
        };

        public DebloatItem(string name, string id, string category, string risk, string description)
        {
            Name = name; Id = id; Category = category; Risk = risk; Description = description;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
