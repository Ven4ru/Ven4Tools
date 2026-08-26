using System.ComponentModel;
using Ven4Tools.Models;

namespace Ven4Tools.ViewModels
{
    /// <summary>
    /// Обёртка над Ven4Tools.Models.ConfigSnapshotInfo (не трогаем — шарится с
    /// ConfigSnapshotService) для per-item состояния «идёт восстановление». Заменяет
    /// оригинальное btn.IsEnabled = false на конкретной нажатой кнопке восстановления:
    /// только эта строка блокируется, остальные снапшоты остаются доступны для
    /// восстановления/удаления — точно как в оригинале.
    /// </summary>
    public sealed class SnapshotRow : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public ConfigSnapshotInfo Info { get; }

        public SnapshotRow(ConfigSnapshotInfo info) => Info = info;

        public string DisplayLabel => Info.DisplayLabel;

        private bool _isRestoring;
        public bool IsRestoring
        {
            get => _isRestoring;
            internal set
            {
                if (_isRestoring == value) return;
                _isRestoring = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRestoring)));
            }
        }
    }
}
