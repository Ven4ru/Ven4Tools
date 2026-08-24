using Ven4Tools.Models;

namespace Ven4Tools.ViewModels
{
    /// <summary>
    /// Строка списка «История изменений каталога» на вкладке «О программе»:
    /// оборачивает <see cref="CatalogChangelogEntry"/> для биндинга, не меняя
    /// саму модель каталога — та общая с загрузкой/подписью каталога, UI-логике
    /// там не место. Данные неизменны после построения записи каталога, поэтому
    /// без INotifyPropertyChanged, как DebloatItem/AppRowViewModel для полей,
    /// не меняющихся после создания. Вынесено из code-behind при переходе на
    /// MVVM (2026-08-25, третья вкладка после пилота DebloaterTab и HistoryTab).
    /// </summary>
    public sealed class ChangelogEntryViewModel
    {
        public string HeaderText { get; }
        public string Message { get; }
        public bool HasMessage { get; }
        public string AddedAppsText { get; }
        public bool HasAddedApps { get; }

        public ChangelogEntryViewModel(CatalogChangelogEntry entry)
        {
            HeaderText = $"v{entry.Version}  ·  {entry.Date}";
            Message = entry.Message;
            HasMessage = !string.IsNullOrEmpty(entry.Message);
            HasAddedApps = entry.AddedApps?.Count > 0;
            AddedAppsText = HasAddedApps ? $"+ {string.Join(", ", entry.AddedApps!)}" : "";
        }
    }
}
