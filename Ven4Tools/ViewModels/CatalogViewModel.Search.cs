using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ven4Tools.Models;
using Ven4Tools.Services;

namespace Ven4Tools.ViewModels
{
    // Поиск по каталогу, фильтры (избранное/профиль/установленные), порядок сортировки
    // и подсказки из внешних источников (winget/Chocolatey). Часть CatalogViewModel.
    public sealed partial class CatalogViewModel
    {
        // ── Поиск / фильтры ─────────────────────────────────────────────────────

        private string _searchText = "";
        public string SearchText
        {
            get => _searchText;
            set
            {
                if (SetField(ref _searchText, value))
                {
                    OnPropertyChanged(nameof(HasSearchText));
                    AppsView.Refresh();
                    _searchDebounce?.Cancel();
                    // AppsView.IsEmpty учитывает текущий фильтр без полного перечисления
                    // представления (сеттер срабатывает на каждое нажатие клавиши в поиске),
                    // в отличие от прежнего Cast<object>().Count() == 0.
                    if (value.Length >= 2 && AppsView.IsEmpty)
                    {
                        _searchDebounce = new CancellationTokenSource();
                        _ = RunSearchSuggestionsAsync(value, _searchDebounce.Token);
                    }
                    else
                    {
                        ShowSuggestionsPanel = false;
                        Suggestions.Clear();
                    }
                }
            }
        }

        public bool HasSearchText => !string.IsNullOrEmpty(SearchText);

        private bool _showFavoritesOnly;
        public bool ShowFavoritesOnly
        {
            get => _showFavoritesOnly;
            set
            {
                if (SetField(ref _showFavoritesOnly, value))
                {
                    OnPropertyChanged(nameof(FavoritesOnlyBrush));
                    AppsView.Refresh();
                }
            }
        }

        // btnFavoritesOnly — обычная Button (не ToggleButton): ClientUITests
        // (Phase1CatalogRemainingTests) вызывает её через AsButton().Invoke(),
        // который требует Invoke-паттерн UIA, а не Toggle. Состояние переключается
        // командой, а не IsChecked.
        public System.Windows.Media.Brush FavoritesOnlyBrush => ShowFavoritesOnly
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 215, 0))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(100, 100, 100));

        private bool _showSuggestionsPanel;
        public bool ShowSuggestionsPanel
        {
            get => _showSuggestionsPanel;
            set => SetField(ref _showSuggestionsPanel, value);
        }

        private string _suggestionsStatus = "";
        public string SuggestionsStatus
        {
            get => _suggestionsStatus;
            set => SetField(ref _suggestionsStatus, value);
        }

        private bool RowFilter(object obj)
        {
            if (obj is not AppRowViewModel row) return false;
            if (!row.MatchesProfile) return false;
            if (ProfileService.Current.HideInstalled && row.IsInstalled) return false;
            if (ShowFavoritesOnly && !row.IsFavorite) return false;
            // Совпадение по имени, описанию и идентификаторам winget/Chocolatey —
            // см. AppRowViewModel.MatchesSearch.
            return row.MatchesSearch(SearchText);
        }

        public void ApplyProfileFilters()
        {
            int modeLevel = ProfileService.Current.CatalogMode switch { "basic" => 0, "extended" => 1, _ => 2 };
            bool compact = ProfileService.Current.CompactMode;
            foreach (var row in Apps)
            {
                // Пользовательские приложения видимы в ЛЮБОМ режиме каталога (см.
                // комментарий у AppRowViewModel.MatchesProfile) — как раньше вело себя
                // вычисление profileOk=true при appId==null в исходном CatalogTab.Search.cs.
                // Профильный фильтр применяем только к каталожным приложениям.
                if (row.IsUserAdded)
                    row.MatchesProfile = true;
                else
                {
                    int appLevel = row.Profile switch { "extended" => 1, "full" => 2, _ => 0 };
                    row.MatchesProfile = appLevel <= modeLevel;
                }
                row.IsCompact = compact;
            }
            ApplySortOrder();
            AppsView.Refresh();
        }

        // Первый SortDescription (CategorySortOrder) не трогаем — порядок категорий
        // фиксированный всегда. Второй ключ (имя внутри категории) появляется только
        // для DefaultSort "alpha"/"category" — иначе (см. оригинальный LoadApps())
        // порядок внутри категории оставался таким же, как в самом каталоге (master.json).
        private void ApplySortOrder()
        {
            while (AppsView.SortDescriptions.Count > 1)
                AppsView.SortDescriptions.RemoveAt(1);

            if (ProfileService.Current.DefaultSort is "alpha" or "category")
                AppsView.SortDescriptions.Add(new SortDescription(nameof(AppRowViewModel.DisplayName), ListSortDirection.Ascending));
        }

        private async Task RunSearchSuggestionsAsync(string query, CancellationToken token)
        {
            try
            {
                await Task.Delay(600, token);
                ShowSuggestionsPanel = true;
                Suggestions.Clear();
                SuggestionsStatus = "⏳ Поиск по источникам...";

                // Если пользователь ввёл название категории («видео», «антивирус»),
                // а не имя пакета — ищем по тегу манифеста. Choco по тегам искать не
                // умеет, поэтому в тег-режиме источник один (winget); слово-категория
                // не является осмысленным именем choco-пакета, так что ничего не теряем.
                var tags = CategorySearchMap.TryGetTags(query);
                if (tags != null)
                {
                    await RunTagSuggestionsAsync(tags, query, token);
                    return;
                }

                var wingetTask = WingetService.SearchAsync(query, token);
                var chocoTask = PackageManagerService.SearchChocoAsync(query, token);
                await Task.WhenAll(wingetTask, chocoTask);
                if (token.IsCancellationRequested) return;

                var winget = await wingetTask;
                var choco = await chocoTask;
                if (winget.Count == 0 && choco.Count == 0)
                {
                    SuggestionsStatus = $"😕 Ничего не найдено по запросу «{query}» ни в одном источнике";
                    return;
                }
                SuggestionsStatus = "";

                foreach (var pkg in winget)
                {
                    var captureId = pkg.Id; var captureName = pkg.Name;
                    Suggestions.Add(new SearchSuggestionViewModel(pkg.Name, $"winget:{pkg.Id}", "📦 Winget",
                        () => AddWingetSuggestion(captureName, captureId)));
                }
                foreach (var (id, name, ver) in choco)
                {
                    var captureId = id;
                    Suggestions.Add(new SearchSuggestionViewModel(name.Length > 0 ? name : id, $"v{ver}", "🍫 Chocolatey",
                        () => AddChocoSuggestion(captureId)));
                }
            }
            catch (OperationCanceledException) { }
            catch (TimeoutException)
            {
                if (!token.IsCancellationRequested)
                    SuggestionsStatus = "⚠ Winget не ответил вовремя";
            }
        }

        // Поиск по тегам категории: winget-вызовы по всем тегам идут параллельно
        // (Task.WhenAll — как у соседнего RunSearchSuggestionsAsync с winget+choco),
        // иначе при 2 тегах окно ожидания растягивалось до 2×UiCallTimeout. Результаты
        // объединяются с дедупликацией по Id. Лимит 15 сохранён после объединения — тот
        // же, что у SearchAsync (окно подсказок рассчитано на короткий список).
        private async Task RunTagSuggestionsAsync(string[] tags, string query, CancellationToken token)
        {
            var results = await Task.WhenAll(tags.Select(t => WingetService.SearchByTagAsync(t, token)));
            if (token.IsCancellationRequested) return;

            var merged = new List<WingetPackage>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var found in results)
                foreach (var pkg in found)
                    if (seen.Add(pkg.Id)) merged.Add(pkg);

            if (merged.Count == 0)
            {
                SuggestionsStatus = $"😕 Ничего не найдено по категории «{query}»";
                return;
            }
            SuggestionsStatus = "";

            foreach (var pkg in merged.Take(15))
            {
                var captureId = pkg.Id; var captureName = pkg.Name;
                Suggestions.Add(new SearchSuggestionViewModel(pkg.Name, $"winget:{pkg.Id}", "📦 Winget",
                    () => AddWingetSuggestion(captureName, captureId)));
            }
        }

        private void AddWingetSuggestion(string name, string id)
        {
            if (_appManager.GetAppById(id) != null) { Log($"ℹ️ {name} уже есть в списке"); return; }
            var app = new AppInfo { Id = id, DisplayName = name, Category = AppCategory.Другое, AlternativeId = id, IsUserAdded = true, SilentArgs = "" };
            AddUserApp(app);
            Log($"➕ Добавлено из winget: {name} ({id})");
            SearchText = "";
        }

        private void AddChocoSuggestion(string id)
        {
            string userId = $"User.{id}";
            if (_appManager.GetAppById(userId) != null) { Log($"ℹ️ {id} уже есть в списке"); return; }
            var app = new AppInfo { Id = userId, DisplayName = id, Category = AppCategory.Другое, ChocoId = id, IsUserAdded = true };
            AddUserApp(app);
            Log($"➕ Добавлено из Chocolatey: {id}");
            SearchText = "";
        }
    }
}
