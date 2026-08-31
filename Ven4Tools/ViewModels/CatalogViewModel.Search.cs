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
                    // Cancel + Dispose предыдущего токена — раньше только отменялся,
                    // объект никогда не освобождался (каждое нажатие клавиши в поиске
                    // создаёт новый CancellationTokenSource).
                    var previousDebounce = _searchDebounce;
                    previousDebounce?.Cancel();
                    previousDebounce?.Dispose();
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
            ? Helpers.BrushResolver.Resolve("AccentColor")
            : Helpers.BrushResolver.Resolve("TextSecondary");

        // HideInstalled/DefaultSort уже читались в RowFilter/ApplySortOrder ниже, но
        // задать их было нечем — ни один XAML-элемент их не менял. Обёртки над
        // ProfileService.Current, тот же Button+Command паттерн, что у избранного.
        public bool HideInstalled
        {
            get => ProfileService.Current.HideInstalled;
            set
            {
                if (ProfileService.Current.HideInstalled == value) return;
                ProfileService.Current.HideInstalled = value;
                ProfileService.Save();
                OnPropertyChanged();
                OnPropertyChanged(nameof(HideInstalledBrush));
                AppsView.Refresh();
            }
        }

        public System.Windows.Media.Brush HideInstalledBrush => HideInstalled
            ? Helpers.BrushResolver.Resolve("AccentColor")
            : Helpers.BrushResolver.Resolve("TextSecondary");

        // DefaultSort исторически хранит "alpha"/"category" (обе ветки ApplySortOrder
        // ведут себя одинаково — сортировка по имени внутри категории) — с точки
        // зрения UI это один переключатель "сортировать по алфавиту вкл/выкл".
        public bool SortAlphabetically
        {
            get => ProfileService.Current.DefaultSort is "alpha" or "category";
            set
            {
                string newValue = value ? "alpha" : "none";
                if (ProfileService.Current.DefaultSort == newValue) return;
                ProfileService.Current.DefaultSort = newValue;
                ProfileService.Save();
                OnPropertyChanged();
                OnPropertyChanged(nameof(SortAlphabeticallyBrush));
                ApplySortOrder();
                AppsView.Refresh();
            }
        }

        public System.Windows.Media.Brush SortAlphabeticallyBrush => SortAlphabetically
            ? Helpers.BrushResolver.Resolve("AccentColor")
            : Helpers.BrushResolver.Resolve("TextSecondary");

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

                // Winget и choco обрабатываются независимо друг от друга — раньше
                // Task.WhenAll пробрасывал первое исключение (например, таймаут
                // winget) и терял уже готовые результаты второго источника,
                // показывая пользователю пустую панель, хотя половина поиска
                // отработала успешно.
                var wingetTask = WingetService.SearchAsync(query, token);
                var chocoTask = PackageManagerService.SearchChocoAsync(query, token);

                List<WingetPackage> winget;
                bool wingetTimedOut = false;
                try { winget = await wingetTask; }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch (TimeoutException) { winget = new List<WingetPackage>(); wingetTimedOut = true; }

                List<(string Id, string Version)> choco;
                bool chocoFailed = false;
                try { choco = await chocoTask; }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch (TimeoutException) { choco = new List<(string, string)>(); chocoFailed = true; }

                if (token.IsCancellationRequested) return;

                if (winget.Count == 0 && choco.Count == 0)
                {
                    SuggestionsStatus = wingetTimedOut
                        ? "⚠ Winget не ответил вовремя"
                        : $"😕 Ничего не найдено по запросу «{query}» ни в одном источнике";
                    return;
                }
                SuggestionsStatus = wingetTimedOut ? "⚠ Winget не ответил вовремя, показаны результаты Chocolatey"
                                   : chocoFailed ? "⚠ Chocolatey не ответил вовремя, показаны результаты Winget"
                                   : "";

                foreach (var pkg in winget)
                {
                    var captureId = pkg.Id; var captureName = pkg.Name;
                    Suggestions.Add(new SearchSuggestionViewModel(pkg.Name, $"winget:{pkg.Id}", "📦 Winget",
                        () => AddWingetSuggestion(captureName, captureId)));
                }
                foreach (var (id, ver) in choco)
                {
                    var captureId = id;
                    Suggestions.Add(new SearchSuggestionViewModel(id, $"v{ver}", "🍫 Chocolatey",
                        () => AddChocoSuggestion(captureId)));
                }
            }
            catch (OperationCanceledException) { }
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
            if (_appManager.GetAppById(id) != null) { Log($"ℹ️ {name} уже есть в списке"); SearchText = ""; return; }
            var app = new AppInfo { Id = id, DisplayName = name, Category = AppCategory.Другое, AlternativeId = id, IsUserAdded = true, SilentArgs = "" };
            AddUserApp(app);
            Log($"➕ Добавлено из winget: {name} ({id})");
            SearchText = "";
        }

        private void AddChocoSuggestion(string id)
        {
            string userId = $"User.{id}";
            if (_appManager.GetAppById(userId) != null) { Log($"ℹ️ {id} уже есть в списке"); SearchText = ""; return; }
            var app = new AppInfo { Id = userId, DisplayName = id, Category = AppCategory.Другое, ChocoId = id, IsUserAdded = true };
            AddUserApp(app);
            Log($"➕ Добавлено из Chocolatey: {id}");
            SearchText = "";
        }
    }
}
