using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Ven4Tools.Models
{
    public class Preset : INotifyPropertyChanged
    {
        public int     Id          { get; set; }
        public string  Name        { get; set; } = "";
        public string  Description { get; set; } = "";
        public List<string> Apps   { get; set; } = new();

        public string AppCountLabel => $"{Apps.Count} прил.";

        // Уведомить UI об изменении названия/описания (после редактирования)
        public void RaiseNameChanged()
        {
            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(Description));
        }

        // Уведомить UI об изменении состава (после обновления списка приложений)
        public void RaiseAppCountChanged() => OnPropertyChanged(nameof(AppCountLabel));

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
