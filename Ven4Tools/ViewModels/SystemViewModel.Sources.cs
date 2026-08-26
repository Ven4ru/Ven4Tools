using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Ven4Tools.Models;
using Ven4Tools.Services;

namespace Ven4Tools.ViewModels
{
    public sealed partial class SystemViewModel
    {
        public ObservableCollection<SourceItem> SourceItems { get; } = new();

        private int _selectedSourceIndex = -1;
        public int SelectedSourceIndex { get => _selectedSourceIndex; set => SetField(ref _selectedSourceIndex, value); }

        // Урок InstalledTab (SetFilterFlag): TwoWay-запись группы RadioButton идёт в
        // порядке, обратном событию Checked — сеттер новой выбранной кнопки получает
        // true ПЕРВЫМ, сосед сбрасывается в false ВТОРЫМ. UpdateSourcePanels() читает
        // соседний флаг НЕМЕДЛЕННО, поэтому одного лишь безусловного вызова здесь мало:
        // после первой записи панели считались бы по ещё не сброшенному соседу и до
        // второй записи показывали бы неверное состояние (а если XAML привяжет только
        // одну из кнопок — неверное состояние осталось бы навсегда). Поэтому взаимное
        // исключение поддерживается самой VM: включение одного режима сразу сбрасывает
        // второй (с уведомлением UI), и вторая TwoWay-запись становится no-op.
        private bool _isGlobalSourceMode = true;
        public bool IsGlobalSourceMode
        {
            get => _isGlobalSourceMode;
            set
            {
                if (_isGlobalSourceMode == value) return;
                SetField(ref _isGlobalSourceMode, value);
                if (value) SetField(ref _isPerCategorySourceMode, false, nameof(IsPerCategorySourceMode));
                UpdateSourcePanels();
            }
        }

        private bool _isPerCategorySourceMode;
        public bool IsPerCategorySourceMode
        {
            get => _isPerCategorySourceMode;
            set
            {
                if (_isPerCategorySourceMode == value) return;
                SetField(ref _isPerCategorySourceMode, value);
                if (value) SetField(ref _isGlobalSourceMode, false, nameof(IsGlobalSourceMode));
                UpdateSourcePanels();
            }
        }

        private bool _showGlobalOrderPanel = true;
        public bool ShowGlobalOrderPanel { get => _showGlobalOrderPanel; private set => SetField(ref _showGlobalOrderPanel, value); }

        private bool _showPerCategoryHint;
        public bool ShowPerCategoryHint { get => _showPerCategoryHint; private set => SetField(ref _showPerCategoryHint, value); }

        private string _sourceOrderStatusText = "";
        public string SourceOrderStatusText { get => _sourceOrderStatusText; private set => SetField(ref _sourceOrderStatusText, value); }

        private void LoadSourceOrderUI()
        {
            var settings = SourceOrderService.Current;
            SetField(ref _isGlobalSourceMode, settings.Mode == "global", nameof(IsGlobalSourceMode));
            SetField(ref _isPerCategorySourceMode, settings.Mode == "per_category", nameof(IsPerCategorySourceMode));

            SourceItems.Clear();
            foreach (var id in settings.GlobalOrder)
                SourceItems.Add(new SourceItem { Id = id, Label = SourceOrderSettings.Labels.GetValueOrDefault(id, id) });

            UpdateSourcePanels();
        }

        private void UpdateSourcePanels()
        {
            ShowGlobalOrderPanel = IsGlobalSourceMode;
            ShowPerCategoryHint  = !IsGlobalSourceMode;
        }

        private void MoveSourceUp()
        {
            int idx = SelectedSourceIndex;
            if (idx <= 0) return;
            SourceItems.Move(idx, idx - 1);
            SelectedSourceIndex = idx - 1;
        }

        private void MoveSourceDown()
        {
            int idx = SelectedSourceIndex;
            if (idx < 0 || idx >= SourceItems.Count - 1) return;
            SourceItems.Move(idx, idx + 1);
            SelectedSourceIndex = idx + 1;
        }

        private void SaveSourceOrder()
        {
            SourceOrderService.Current.Mode        = IsGlobalSourceMode ? "global" : "per_category";
            SourceOrderService.Current.GlobalOrder = SourceItems.Select(i => i.Id).ToList();
            SourceOrderService.Save();

            SourceOrderStatusText = $"✅ Сохранено {System.DateTime.Now:HH:mm:ss} — изменится при следующем открытии каталога";
            AppLogger.Write("🔀 Порядок источников сохранён");
        }
    }
}
