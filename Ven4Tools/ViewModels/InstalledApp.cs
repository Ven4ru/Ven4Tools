using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Ven4Tools.ViewModels
{
    /// <summary>
    /// Одна строка списка установленных приложений. Перенесено из
    /// Ven4Tools/Views/Tabs/InstalledTab.xaml.cs при MVVM-миграции (2026-08-26,
    /// седьмая вкладка после Debloater/History/About/Activation/Network/Office)
    /// без изменения тела.
    /// </summary>
    public class InstalledApp : INotifyPropertyChanged
    {
        public string Name      { get; set; } = "";
        public string WingetId  { get; set; } = "";
        public string Version   { get; set; } = "";

        private string _available = "";
        public string Available
        {
            get => _available;
            set { _available = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasUpdate)); }
        }

        public string Source    { get; set; } = "";

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set { if (_isSelected == value) return; _isSelected = value; OnPropertyChanged(); }
        }

        private bool _isProcessing;
        public bool IsProcessing
        {
            get => _isProcessing;
            set { _isProcessing = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanAct)); }
        }

        public bool HasUpdate        => !string.IsNullOrWhiteSpace(Available) && Available != "Unknown";
        public bool CanAct           => !IsProcessing;
        public bool IsVerified       => Source.Equals("winget", StringComparison.OrdinalIgnoreCase)
                                     || Source.Equals("msstore", StringComparison.OrdinalIgnoreCase);
        public bool IsUnknownSource  => string.IsNullOrWhiteSpace(Source) || Source.Equals("Unknown", StringComparison.OrdinalIgnoreCase);

        public string SourceDisplay
        {
            get
            {
                if (string.IsNullOrWhiteSpace(Source) || Source.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                    return "❓ Неизвестный";
                if (Source.Equals("winget", StringComparison.OrdinalIgnoreCase))
                    return "✔ winget";
                if (Source.Equals("msstore", StringComparison.OrdinalIgnoreCase))
                    return "✔ Store";
                return Source;
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
