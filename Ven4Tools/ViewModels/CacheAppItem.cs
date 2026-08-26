using System.ComponentModel;

namespace Ven4Tools.ViewModels
{
    /// <summary>
    /// Строка списка приложений для офлайн-кэширования. В оригинальном code-behind —
    /// private nested class без INotifyPropertyChanged; синхронизация с UI шла через
    /// ручной listCacheApps.Items.Refresh() после программного изменения IsSelected
    /// («Выбрать все» / «Сброс»). В MVVM такого механизма нет — IsSelected обязан
    /// поднимать PropertyChanged сам, иначе программные изменения не отразятся
    /// в уже отрисованных чекбоксах.
    /// </summary>
    public sealed class CacheAppItem : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public required string Id          { get; init; }
        public required string DisplayName { get; init; }
        public required string DownloadUrl { get; init; }
        public required string Sha256      { get; init; }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }
    }
}
