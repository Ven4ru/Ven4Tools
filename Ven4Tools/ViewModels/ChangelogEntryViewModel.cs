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
            // Заметки к версиям каталога приходят вместе с каталогом, а не с программой,
            // поэтому в языковом пакете их нет. При английском интерфейсе берётся английская
            // заметка из каталога; у старых записей её нет — тогда заметка показывается,
            // только если перевод для неё всё же нашёлся: строка на чужом языке посреди
            // переведённого окна хуже, чем её отсутствие.
            string message = entry.Message ?? "";
            if (Ven4Tools.Localization.UiTranslator.IsActive)
            {
                string translated = !string.IsNullOrWhiteSpace(entry.MessageEn) ? entry.MessageEn : Tr(message);
                message = Ven4Tools.Localization.LanguagePack.HasCyrillic(translated) ? "" : translated;
            }
            Message = message;
            HasMessage = !string.IsNullOrEmpty(message);
            HasAddedApps = entry.AddedApps?.Count > 0;
            AddedAppsText = HasAddedApps ? $"+ {string.Join(", ", entry.AddedApps!)}" : "";
        }
    }
}
