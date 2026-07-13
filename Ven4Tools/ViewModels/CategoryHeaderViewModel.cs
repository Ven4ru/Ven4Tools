using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Ven4Tools.Models;
using Ven4Tools.Services;

namespace Ven4Tools.ViewModels
{
    public sealed class SourceOrderOption
    {
        public string Label { get; }
        public string SourceId { get; }
        public SourceOrderOption(string label, string sourceId) { Label = label; SourceId = sourceId; }
    }

    // Заголовок группы категории в GroupStyle. В обычном режиме — просто текст,
    // в режиме SourceOrderService.Mode == "per_category" — ещё и комбобокс выбора
    // приоритетного источника для этой категории (раньше строился императивно в
    // CatalogTab.Catalog.cs → ApplyCategorySourceHeaders).
    public sealed class CategoryHeaderViewModel : INotifyPropertyChanged
    {
        public string CategoryName { get; }
        public string Label { get; }
        public ObservableCollection<SourceOrderOption> Options { get; } = new();

        public CategoryHeaderViewModel(string categoryName, string label)
        {
            CategoryName = categoryName;
            Label = label;

            Options.Add(new SourceOrderOption("🔀 Глобальный", ""));
            foreach (var srcId in SourceOrderSettings.AllSources)
                Options.Add(new SourceOrderOption(SourceOrderSettings.Labels[srcId], srcId));

            string current = SourceOrderService.GetCategoryPrimary(label);
            _selected = string.IsNullOrEmpty(current)
                ? Options[0]
                : (System.Linq.Enumerable.FirstOrDefault(Options, o => o.SourceId == current) ?? Options[0]);
        }

        private bool _showCombo;
        public bool ShowCombo
        {
            get => _showCombo;
            set => SetField(ref _showCombo, value);
        }

        private SourceOrderOption _selected;
        public SourceOrderOption Selected
        {
            get => _selected;
            set
            {
                if (SetField(ref _selected, value))
                {
                    SourceOrderService.SetCategoryPrimary(Label, value.SourceId);
                    SourceOrderService.Save();
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }
    }
}
