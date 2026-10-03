using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using Ven4Tools.Helpers;
using Ven4Tools.Services;

namespace Ven4Tools.ViewModels
{
    // Панель «Ваш набор» и кнопки-категории нового интерфейса каталога. Часть
    // CatalogViewModel. Прежний вид каталога этими данными не пользуется: там выбор
    // виден только отметками в списке и счётчиком внизу.
    public sealed partial class CatalogViewModel
    {
        // ── Ваш набор ────────────────────────────────────────────────────────────

        /// <summary>Отмеченные приложения в порядке, в котором их отмечали.</summary>
        public ObservableCollection<AppRowViewModel> SelectedApps { get; } = new();

        public bool HasSelection => SelectedApps.Count > 0;

        /// <summary>Суммарный размер загрузки по набору: «≈ 233 МБ», «≈ 233 МБ и 2 без размера».</summary>
        public string SelectionTotalText => DescribeTotal(SelectedApps.Select(a => a.CatalogSizeText));

        /// <summary>Код набора вида <c>V4T:id,id</c> — тот же формат, что принимает «Набор с сайта».</summary>
        public string SelectionCode => SitePresetService.BuildCode(
            SelectedApps.Where(a => !a.IsUserAdded).Select(a => a.AppId));

        private RelayCommand? _removeFromSelectionCommand;
        public RelayCommand RemoveFromSelectionCommand => _removeFromSelectionCommand ??= new RelayCommand(p =>
        {
            if (p is AppRowViewModel row) row.IsSelected = false;
        });

        private RelayCommand? _clearSelectionCommand;
        public RelayCommand ClearSelectionCommand => _clearSelectionCommand ??= new RelayCommand(_ =>
        {
            foreach (var row in SelectedApps.ToList()) row.IsSelected = false;
        }, _ => HasSelection && !IsInstalling);

        private RelayCommand? _copySelectionCodeCommand;
        public RelayCommand CopySelectionCodeCommand => _copySelectionCodeCommand ??= new RelayCommand(_ =>
        {
            string code = SelectionCode;
            if (code.Length == 0) return;
            try
            {
                Clipboard.SetText(code);
                Log("📋 Код набора скопирован в буфер обмена");
            }
            catch (Exception ex)
            {
                // Буфер обмена бывает занят другим процессом — это не повод ронять вкладку.
                Log($"⚠ Не удалось скопировать код набора: {ex.Message}");
            }
        }, _ => SelectionCode.Length > 0);

        private RelayCommand? _setFromInstalledCommand;
        /// <summary>
        /// «Набор из установленного»: показывает, что из каталога уже стоит на этом
        /// компьютере, с кодом набора и файлом ответа. Отметки в каталоге не меняет —
        /// отмеченные установленные программы кнопка «Установить» стала бы ставить заново.
        /// </summary>
        public RelayCommand SetFromInstalledCommand => _setFromInstalledCommand ??= new RelayCommand(_ =>
        {
            var set = InstalledSetBuilder.Build(
                Apps.Where(a => !a.IsUserAdded).Select(a => (a.AppId, a.DisplayName, a.IsInstalled)));
            if (set.AppIds.Count == 0)
            {
                MessageBox.Show(
                    "Среди программ каталога установленных не найдено.\n\n" +
                    "Если каталог открыт только что, дождитесь окончания проверки и повторите.",
                    "Набор из установленного", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            Log($"📋 Набор из установленного: программ — {set.AppIds.Count}");
            new Views.InstalledSetWindow(set) { Owner = OwnerWindowProvider?.Invoke() }.ShowDialog();
        });

        /// <summary>
        /// Готовый набор с «Обзора»: добавляет его программы к уже отмеченным. Ждёт
        /// загрузки каталога — набор могут выбрать раньше, чем каталог открыт впервые.
        /// </summary>
        public async System.Threading.Tasks.Task ApplyAppSetAsync(IReadOnlyList<string> appIds, string setTitle)
        {
            await EnsureLoadedAsync();

            int marked = 0;
            var skipped = new List<string>();
            foreach (string id in appIds)
            {
                var row = Apps.FirstOrDefault(a => string.Equals(a.AppId, id, StringComparison.OrdinalIgnoreCase));
                if (row is { IsSelectable: true }) { row.IsSelected = true; marked++; }
                else skipped.Add(row?.DisplayName ?? id);
            }

            Log($"📋 Набор «{setTitle}»: отмечено {marked}" +
                (skipped.Count > 0 ? $", пропущено (нет в каталоге или недоступно): {string.Join(", ", skipped)}" : ""));
        }

        /// <summary>
        /// Приводит <see cref="SelectedApps"/> в соответствие с отметками строк. Порядок
        /// уже выбранных не меняется — новые встают в конец, снятые убираются.
        /// </summary>
        private void RefreshSelection()
        {
            var selected = Apps.Where(a => a.IsSelected).ToList();
            var selectedSet = new HashSet<AppRowViewModel>(selected);

            for (int i = SelectedApps.Count - 1; i >= 0; i--)
                if (!selectedSet.Contains(SelectedApps[i])) SelectedApps.RemoveAt(i);

            var present = new HashSet<AppRowViewModel>(SelectedApps);
            foreach (var row in selected)
                if (present.Add(row)) SelectedApps.Add(row);

            OnPropertyChanged(nameof(SelectedCount));
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(SelectionTotalText));
            OnPropertyChanged(nameof(SelectionCode));
        }

        internal static string DescribeTotal(IEnumerable<string?> sizes)
        {
            double total = 0;
            int known = 0, unknown = 0;
            foreach (string? size in sizes)
            {
                if (CatalogSize.TryParseMegabytes(size, out double megabytes)) { total += megabytes; known++; }
                else unknown++;
            }

            if (known == 0) return unknown == 0 ? "—" : "размер неизвестен";
            string text = "≈ " + CatalogSize.Format(total);
            return unknown == 0 ? text : $"{text} и {unknown} без размера";
        }

        // ── Категории ────────────────────────────────────────────────────────────

        public ObservableCollection<CategoryChipViewModel> CategoryChips { get; } = new();

        private string? _categoryFilter;
        /// <summary>
        /// Категория, которой ограничен список; null — показываются все. Поиск ищет по
        /// всему каталогу, поэтому при вводе запроса ограничение снимается само.
        /// </summary>
        public string? CategoryFilter
        {
            get => _categoryFilter;
            set
            {
                if (!SetField(ref _categoryFilter, value)) return;
                foreach (var chip in CategoryChips) chip.IsActive = chip.Key == value;
                OnPropertyChanged(nameof(ActiveCategoryChip));
                AppsView.Refresh();
            }
        }

        /// <summary>
        /// Выбранная кнопка-категория — для списка категорий (SelectedItem). null от
        /// списка игнорируется: он приходит, когда набор кнопок пересобирается, а не
        /// когда пользователь что-то выбрал.
        /// </summary>
        public CategoryChipViewModel? ActiveCategoryChip
        {
            get => CategoryChips.FirstOrDefault(chip => chip.Key == _categoryFilter);
            set
            {
                if (value != null) SelectCategoryCommand.Execute(value);
            }
        }

        private RelayCommand? _selectCategoryCommand;
        public RelayCommand SelectCategoryCommand => _selectCategoryCommand ??= new RelayCommand(p =>
        {
            if (p is not CategoryChipViewModel chip) return;
            // Выбор категории и поиск по всему каталогу исключают друг друга.
            if (chip.Key != null && HasSearchText) SearchText = "";
            CategoryFilter = chip.Key;
        });

        /// <summary>
        /// Пересчитывает кнопки-категории по строкам каталога, видимым в текущем режиме
        /// («Базовый»/«Расширенный»/«Полный»). Порядок — тот же фиксированный, что у групп.
        /// </summary>
        private void RefreshCategoryChips()
        {
            var visible = Apps.Where(a => a.MatchesProfile).ToList();
            var wanted = new List<(string? Key, string Label, int Count)> { (null, "Все", visible.Count) };
            wanted.AddRange(visible
                .GroupBy(a => a.CategoryString)
                .OrderBy(g => g.First().CategorySortOrder)
                .Select(g => ((string?)g.Key, g.Key, g.Count())));

            bool sameSet = wanted.Count == CategoryChips.Count
                && wanted.Zip(CategoryChips, (w, chip) => w.Key == chip.Key).All(same => same);
            if (sameSet)
            {
                for (int i = 0; i < wanted.Count; i++) CategoryChips[i].Count = wanted[i].Count;
            }
            else
            {
                CategoryChips.Clear();
                foreach (var (key, label, count) in wanted)
                    CategoryChips.Add(new CategoryChipViewModel(key, label, count) { IsActive = key == _categoryFilter });
                OnPropertyChanged(nameof(ActiveCategoryChip));
            }

            // Категория опустела (сменили режим каталога, скрыли последнюю программу) —
            // иначе список остался бы пустым без видимой причины.
            if (_categoryFilter != null && wanted.All(w => w.Key != _categoryFilter))
                CategoryFilter = null;
        }

        private void InitSelectionTracking()
        {
            Apps.CollectionChanged += (_, _) =>
            {
                RefreshSelection();
                RefreshCategoryChips();
            };
        }
    }
}
