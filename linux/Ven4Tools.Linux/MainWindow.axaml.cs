using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Ven4Tools.Linux.Models;
using Ven4Tools.Linux.Services;
using Ven4Tools.Linux.Views;
using Ven4Tools.Services;

namespace Ven4Tools.Linux;

public partial class MainWindow : Window
{
    // Порядок имён как в клиенте Windows: латиница, затем кириллица. Правила ICU для
    // ru-RU ставят кириллицу первой, поэтому сравнение нейтральное
    private static readonly StringComparer NameOrder = StringComparer.InvariantCultureIgnoreCase;

    // Английские подписи готовых наборов: в ReadySets они только на русском
    private static readonly Dictionary<string, string> SetDescriptionsEn = new()
    {
        ["home"] = "Browser, messenger, video, archiver, documents and PDF.",
        ["work"] = "Browser, office, PDF, video calls, messenger, screenshots and remote access.",
        ["games"] = "Game stores, voice chat, recording and GPU monitoring.",
        ["dev"] = "Code editor, Git, Python, Node.js, Postman and a text editor.",
    };

    // Порядок категорий — как у кнопок-категорий клиента Windows; новые категории каталога встают в конец
    private static readonly string[] CategoryOrder =
    {
        "Браузеры", "Офис", "Графика", "Разработка", "ИИ", "Мессенджеры", "Мультимедиа",
        "Системные", "Система Linux", "Игровые сервисы", "Драйверпаки", "Компоненты", "Другое"
    };

    private List<AppItem> _items = new();
    private LinuxPackageMap _map = new();
    // Основной источник - каталог Linux; null - запасной путь через master.json + linux-packages.json
    private LinuxCatalog? _linux;
    private List<CatalogApp> _master = new();
    // Названия программ каталога Windows - для строки «нет под Linux» в наборах
    private Dictionary<string, string> _windowsNames = new();
    private string _catalogText = "";
    // Выбранная категория: русское название из каталога, пустая строка — «Все»
    private string _category = "";
    private bool _chipsUpdating;
    private readonly List<(Button Button, TabItem Tab)> _nav = new();

    // Идёт установка, очистка или смена оформления: индикатор в шапке показывает это, как в клиенте Windows
    private bool _busyFlag;
    private bool Busy
    {
        get => _busyFlag;
        set { _busyFlag = value; UpdateActivity(); }
    }

    public MainWindow()
    {
        InitializeComponent();
        ApplyTexts();
        InitToolTabs();
        BuildNav();
        // Шапка показывает название открытого раздела, как заголовок страницы в клиенте Windows
        Tabs.SelectionChanged += (_, _) =>
        {
            TitleText.Text = (Tabs.SelectedItem as TabItem)?.Header as string ?? "";
            foreach (var (button, tab) in _nav) button.Classes.Set("active", ReferenceEquals(tab, Tabs.SelectedItem));
            // «Работает» и свободное место — на момент открытия раздела, а не запуска программы
            if (ReferenceEquals(Tabs.SelectedItem, SystemTab)) ShowSystemInfo();
        };
        TitleText.Text = (Tabs.SelectedItem as TabItem)?.Header as string ?? "";
        SearchBox.TextChanged += (_, _) => ApplyFilter();
        CategoryChips.SelectionChanged += (_, _) =>
        {
            if (_chipsUpdating || CategoryChips.SelectedItem is not CategoryChip chip) return;
            _category = chip.Key;
            ApplyFilter();
        };
        UnavailableBox.IsCheckedChanged += (_, _) => ApplyFilter();
        FavoritesOnlyBox.IsCheckedChanged += (_, _) => ApplyFilter();
        ActivityToggle.IsCheckedChanged += (_, _) => UpdateActivityPanel();
        LanguageBox.SelectionChanged += OnLanguageChanged;
        Opened += async (_, _) => await LoadAsync();
    }

    private void ApplyTexts()
    {
        Title = L.T("Ven4Tools для Linux");
        StatusLine.Text = L.T("Загружаю каталог…");
        CatalogTab.Header = L.T("Каталог");
        SetsTab.Header = L.T("Наборы");
        UpdatesTab.Header = L.T("Обновления");
        SettingsTab.Header = L.T("Настройки");
        SearchBox.Watermark = L.T("Поиск: название или описание программы");
        UnavailableBox.Content = L.T("Недоступные");
        ToolTip.SetTip(UnavailableBox, L.T("Показать и программы, которые сейчас поставить нельзя"));
        ToolTip.SetTip(FavoritesOnlyBox, L.T("Только избранное"));
        ToolTip.SetTip(GearButton, L.T("Настройки"));
        FlathubTitle.Text = L.T("Flathub не включён");
        FlathubHint.Text = L.T("Большая часть программ ставится с Flathub. Система спросит пароль один раз.");
        FlatpakButton.Content = L.T("Включить Flathub");
        SetPanelTitle.Text = L.T("Ваш набор");
        SetPanelHint.Text = L.T("Добавьте программы из каталога — они установятся одна за другой без вопросов.");
        SetPanelNote.Text = L.T("Flatpak ставит программы в ваш профиль без пароля. Для Snap система спросит пароль.");
        ClearSelectionButton.Content = L.T("Снять отметки");
        PresetPanelTitle.Text = L.T("Наборы");
        PresetNameBox.Watermark = L.T("Название набора");
        SavePresetButton.Content = L.T("Сохранить выбор");
        OpenSetsButton.Content = L.T("Все наборы");
        VersionText.Text = typeof(MainWindow).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "";
        NetText.Text = L.T("Проверяю связь…");
        GearIcon.Data = Icons.Gear;
        FavoritesOnlyIcon.Data = Icons.StarFilled;
        UpdateActivity();
        UpdateActivityPanel();
        CheckUpdatesButton.Content = L.T("Проверить обновления");
        UpdateAllButton.Content = L.T("Обновить всё");
        UpdatesText.Text = L.T("Нажмите «Проверить обновления».");
        LanguageLabel.Text = L.T("Язык интерфейса");
        LanguageHint.Text = L.T("Перезапустите программу, чтобы сменить язык.");
        ProfileLabel.Text = L.T("Профиль");
        ProfileHint.Text = L.T("Избранное и ваши наборы. Архив совместим с Ven4Tools для Windows.");
        ExportButton.Content = L.T("Экспорт профиля");
        ImportButton.Content = L.T("Импорт профиля");
        LogLabel.Text = L.T("Центр активности");
        LanguageBox.ItemsSource = new[] { "Русский", "English" };
        LanguageBox.SelectedIndex = L.IsEnglish ? 1 : 0;
        UpdateSelectionButton();
    }

    // ---------- Боковое меню, шапка, «Центр активности» ----------

    private void BuildNav()
    {
        foreach (var (tab, icon) in new (TabItem, Avalonia.Media.Geometry)[]
                 {
                     (CatalogTab, Icons.Catalog), (SetsTab, Icons.Sets), (UpdatesTab, Icons.Updates),
                     (LookTab, Icons.Look), (SystemTab, Icons.Computer), (AutostartTab, Icons.Autostart),
                     (SettingsTab, Icons.Gear),
                 })
        {
            // Настройки — под чертой, отдельно от разделов, как в клиенте Windows
            if (ReferenceEquals(tab, SettingsTab))
                NavPanel.Children.Add(new Border
                {
                    Height = 1, Margin = new Avalonia.Thickness(8, 10),
                    Background = (Avalonia.Media.IBrush)this.FindResource("BorderBrush")!
                });
            var button = new Button
            {
                Classes = { "nav" },
                Content = new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 12,
                    Children =
                    {
                        new PathIcon { Data = icon, Width = 16, Height = 16 },
                        new TextBlock { Text = tab.Header as string, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center }
                    }
                }
            };
            var target = tab;
            button.Click += (_, _) => Tabs.SelectedItem = target;
            button.Classes.Set("active", ReferenceEquals(tab, Tabs.SelectedItem));
            NavPanel.Children.Add(button);
            _nav.Add((button, tab));
        }
    }

    private void OnOpenSettings(object? sender, RoutedEventArgs e) => Tabs.SelectedItem = SettingsTab;

    private void OnOpenSets(object? sender, RoutedEventArgs e) => Tabs.SelectedItem = SetsTab;

    private void UpdateActivity()
    {
        ActivityText.Text = Busy ? L.T("Идёт работа…") : L.T("Нет активных задач");
        ActivityDot.Fill = (Avalonia.Media.IBrush)this.FindResource(Busy ? "AccentColor" : "TextSecondary")!;
    }

    private void UpdateActivityPanel()
    {
        var open = ActivityToggle.IsChecked == true;
        LogBox.IsVisible = open;
        ActivityChevron.Data = open ? Icons.ChevronDown : Icons.ChevronRight;
    }

    // Связь: каталог пришёл с сервера — интернет есть; из кэша или встроенный — нет
    private void ShowNetwork(bool online)
    {
        NetText.Text = online ? L.T("Интернет доступен") : L.T("Нет связи с сервером");
        NetText.Foreground = NetDot.Fill =
            (Avalonia.Media.IBrush)this.FindResource(online ? "StatusSuccess" : "StatusWarning")!;
    }

    // ---------- Загрузка ----------

    private async Task LoadAsync()
    {
        try
        {
            _windowsNames = CatalogLoader.LoadWindowsNames();
            string source;
            if (await CatalogLoader.LoadLinuxAsync(Log) is { } linux)
            {
                _linux = linux.Catalog;
                source = linux.Source;
                _catalogText = L.IsEnglish
                    ? $"Linux catalog v{_linux.Version} ({source}), apps: {_linux.Apps.Count}"
                    : $"Каталог Linux v{_linux.Version} ({source}), программ: {_linux.Apps.Count}";
            }
            else
            {
                _map = CatalogLoader.LoadPackageMap();
                var (catalog, masterSource) = await CatalogLoader.LoadAsync(Log);
                _master = catalog.Apps;
                source = masterSource;
                _catalogText = L.IsEnglish
                    ? $"Catalog v{catalog.Version} ({source}), apps: {catalog.Apps.Count}"
                    : $"Каталог v{catalog.Version} ({source}), программ: {catalog.Apps.Count}";
            }
            ShowNetwork(CatalogLoader.ServerReachable);
            BuildItems();
            await RefreshInstalledAsync();
            UpdateStatus();
        }
        catch (Exception ex)
        {
            StatusLine.Text = L.T("Ошибка: ") + ex.Message;
            Log(ex.ToString());
        }
    }

    private void UpdateStatus()
    {
        var ready = _items.Count(i => i.HasLinuxPackage);
        StatusLine.Text = L.IsEnglish
            ? $"{Distro.Name} · {_catalogText}, installable here: {ready}. Flatpak: {(Flatpak.IsAvailable ? "yes" : "no")}, Snap: {(Snap.IsAvailable ? "yes" : "no")}."
            : $"{Distro.Name} · {_catalogText}, можно поставить в этой системе: {ready}. Flatpak: {(Flatpak.IsAvailable ? "есть" : "нет")}, Snap: {(Snap.IsAvailable ? "есть" : "нет")}.";
        FlathubCard.IsVisible = !Flatpak.IsAvailable;
    }

    private void BuildItems()
    {
        _items = _linux is not null
            ? _linux.Apps.Select(FromLinux).ToList()
            : _master.Select(a =>
            {
                _map.Apps.TryGetValue(a.Id, out var pkg);
                return new AppItem { App = a, Package = pkg, Backend = PickBackend(pkg) };
            }).ToList();
        foreach (var i in _items) i.IsFavorite = IsFavorite(i);

        ApplyFilter();
        BuildSets();
    }

    private static AppItem FromLinux(LinuxCatalogApp a)
    {
        var pkg = new LinuxPackage
        {
            Flatpak = a.Flatpak, FlatpakVerified = a.Official,
            Snap = a.Snap, SnapPublisher = a.SnapPublisher, SnapClassic = a.SnapClassic
        };
        var unavailable = !string.Equals(a.Status, "ok", StringComparison.OrdinalIgnoreCase);
        return new AppItem
        {
            App = new CatalogApp
            {
                Id = a.Id, Name = L.IsEnglish && a.NameEn is { Length: > 0 } en ? en : a.Name,
                Category = a.Category, Description = a.Description, DescriptionEn = a.DescriptionEn
            },
            Package = pkg,
            Backend = unavailable ? null : PickBackend(pkg),
            Replaces = a.Replaces,
            Unavailable = unavailable,
            CheckedDate = a.Checked?.Date,
            SnapOfficial = a.Snap is not null && a.Flatpak is null && a.Official,
        };
    }

    // Избранное из клиента Windows хранит id каталога Windows: PeaZip отмечается, если отмечен 7-Zip
    private static bool IsFavorite(AppItem i) => i.KnownIds.Any(UserData.Favorites.Contains);

    private static int CategoryRank(string category)
    {
        var i = Array.IndexOf(CategoryOrder, category);
        return i < 0 ? CategoryOrder.Length : i;
    }

    // Flatpak в приоритете: ставит без пароля и одинаково на любом дистрибутиве
    private static BackendKind? PickBackend(LinuxPackage? pkg)
    {
        if (pkg is null) return null;
        if (pkg.Flatpak is not null && Flatpak.IsAvailable) return BackendKind.Flatpak;
        if (pkg.Snap is not null && Snap.IsAvailable) return BackendKind.Snap;
        return null;
    }

    private async Task RefreshInstalledAsync()
    {
        var flatpaks = await Flatpak.ListInstalledAsync();
        var snaps = await Snap.ListInstalledAsync();
        foreach (var i in _items)
        {
            i.IsInstalled = i.Backend switch
            {
                BackendKind.Flatpak => flatpaks.Contains(i.PackageId!),
                BackendKind.Snap => snaps.Contains(i.PackageId!),
                _ => false
            };
        }
    }

    // ---------- Каталог ----------

    private void ApplyFilter()
    {
        var q = SearchBox.Text?.Trim() ?? "";
        var showUnavailable = UnavailableBox.IsChecked == true;
        var favOnly = FavoritesOnlyBox.IsChecked == true;

        // Поиск и переключатели сужают каталог; категория выбирается уже среди найденного
        var found = _items
            .Where(i => showUnavailable || i.ShownByDefault)
            .Where(i => !favOnly || i.IsFavorite)
            .Where(i => q.Length == 0
                        || i.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                        || i.Description.Contains(q, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Кнопки-категории с числом программ, как у клиента Windows: «Все 58», «Браузеры 9»…
        var byCategory = found.GroupBy(i => i.App.Category)
            .OrderBy(g => CategoryRank(g.Key)).ThenBy(g => g.Key, NameOrder).ToList();
        if (_category.Length > 0 && byCategory.All(g => g.Key != _category)) _category = "";
        var chips = new List<CategoryChip> { new() { Key = "", Label = L.T("Все"), Count = found.Count } };
        chips.AddRange(byCategory.Select(g => new CategoryChip { Key = g.Key, Label = L.T(g.Key), Count = g.Count() }));
        _chipsUpdating = true;
        CategoryChips.ItemsSource = chips;
        CategoryChips.SelectedItem = chips.First(c => c.Key == _category);
        _chipsUpdating = false;

        // Карточки — группами по категориям, внутри группы по имени
        GroupList.ItemsSource = byCategory
            .Where(g => _category.Length == 0 || g.Key == _category)
            .Select(g => new CatalogGroup { Header = L.T(g.Key), Items = g.OrderBy(i => i.Name, NameOrder).ToList() })
            .ToList();
    }

    private void OnSelectionChanged(object? sender, RoutedEventArgs e) => UpdateSelectionButton();

    private void UpdateSelectionButton()
    {
        var n = _items.Count(i => i.IsSelected);
        InstallSelectedButton.Content = L.T("Установить") + $" ({n})";
        InstallSelectedButton.IsEnabled = n > 0 && !Busy;
        ClearSelectionButton.IsEnabled = n > 0;
        SelCountText.Text = L.T("Выбрано: ") + n;
        SavePresetButton.IsEnabled = n > 0;
    }

    private void OnClearSelection(object? sender, RoutedEventArgs e)
    {
        foreach (var i in _items) i.IsSelected = false;
        UpdateSelectionButton();
    }

    private void OnToggleFavorite(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is not AppItem item) return;
        item.IsFavorite = !item.IsFavorite;
        if (item.IsFavorite) UserData.Favorites.Add(item.Id);
        else foreach (var id in item.KnownIds) UserData.Favorites.Remove(id);
        UserData.SaveFavorites();
        if (FavoritesOnlyBox.IsChecked == true) ApplyFilter();
    }

    private async void OnAppAction(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is not AppItem item || item.PackageId is null) return;
        await RunQueueAsync(new[] { item }, remove: item.IsInstalled);
    }

    private async void OnInstallSelected(object? sender, RoutedEventArgs e)
    {
        // Уже установленные из очереди выпадают - ставить их повторно незачем
        var queue = _items.Where(i => i.IsSelected && !i.IsInstalled).ToList();
        foreach (var i in _items) i.IsSelected = false;
        await RunQueueAsync(queue, remove: false);
    }

    private async Task RunQueueAsync(IReadOnlyCollection<AppItem> queue, bool remove)
    {
        if (queue.Count == 0 || Busy) return;
        Busy = true;
        foreach (var i in queue) i.IsBusy = true;
        UpdateSelectionButton();
        var names = string.Join(", ", queue.Select(i => i.Name));
        Log("--- " + (remove ? (L.IsEnglish ? "Removing: " : "Удаление: ") : (L.IsEnglish ? "Installing: " : "Установка: ")) + names);
        try
        {
            var ok = await InstallQueue.RunAsync(queue, remove, Log);
            Log(ok ? L.T("Готово.") : L.T("Не получилось, подробности выше."));
        }
        catch (Exception ex)
        {
            Log(L.T("Ошибка: ") + ex.Message);
        }
        finally
        {
            // Сбой опроса установленных (flatpak/snap не запустился) не должен оставить
            // очередь «занятой» навсегда — и не должен уронить программу.
            try { await RefreshInstalledAsync(); }
            catch (Exception ex) { Log(L.T("Ошибка: ") + ex.Message); }
            foreach (var i in queue) i.IsBusy = false;
            Busy = false;
            UpdateSelectionButton();
        }
    }

    private async void OnSetupFlatpak(object? sender, RoutedEventArgs e)
    {
        FlatpakButton.IsEnabled = false;
        Log("--- Flatpak");
        // async void: без catch исключение (например, нет pkexec) роняет программу,
        // а без finally кнопка оставалась выключенной.
        try
        {
            var ok = await Flatpak.SetupAsync(Log);
            Log(ok ? L.T("Готово.") : L.T("Не получилось, подробности выше."));
            if (ok)
            {
                // Источник у строк сменился - пересобираем список целиком
                BuildItems();
                await RefreshInstalledAsync();
                UpdateStatus();
            }
        }
        catch (Exception ex) { Log(L.T("Ошибка: ") + ex.Message); }
        finally { FlatpakButton.IsEnabled = true; }
    }

    // ---------- Наборы ----------

    private void BuildSets()
    {
        // Наборы клиента Windows и профили из него хранят id каталога Windows; замены (PeaZip вместо 7-Zip)
        // находятся по полю replaces каталога Linux
        var byId = new Dictionary<string, AppItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var i in _items) byId.TryAdd(i.Id, i);
        foreach (var i in _items)
            foreach (var r in i.Replaces) byId.TryAdd(r.Id, i);
        SetCard Card(string title, string description, IEnumerable<string> ids, UserPreset? preset)
        {
            var list = ids.Distinct().ToList();
            return new SetCard
            {
                Title = title,
                Description = description,
                // Аналоги (Epic и GOG -> Heroic) ставятся одним пакетом, дубли убираются
                Available = list.Where(id => byId.TryGetValue(id, out var i) && i.HasLinuxPackage)
                    .Select(id => byId[id]).DistinctBy(i => i.PackageId).ToList(),
                Missing = list.Where(id => !byId.TryGetValue(id, out var i) || i.Unavailable || i.Package?.Flatpak is null && !i.HasLinuxPackage)
                    .Select(id => byId.TryGetValue(id, out var i) ? i.Name : _windowsNames.GetValueOrDefault(id, id)).ToList(),
                FlathubOnly = list.Where(id => byId.TryGetValue(id, out var i) && !i.Unavailable && !i.HasLinuxPackage && i.Package?.Flatpak is not null)
                    .Select(id => byId[id].LinuxName).ToList(),
                UserPreset = preset
            };
        }

        var cards = ReadySets.All
            .Select(s => Card(L.T(s.Title),
                L.IsEnglish && SetDescriptionsEn.TryGetValue(s.Key, out var en) ? en : s.Description,
                s.AppIds, null))
            .Concat(UserData.Presets.Select(p => Card("★ " + p.Name, p.Description, p.Apps, p)))
            .ToList();
        SetList.ItemsSource = cards;
    }

    private async void OnSetAction(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: SetCard card, Tag: string action }) return;
        switch (action)
        {
            case "install":
                await RunQueueAsync(card.Available.Where(i => !i.IsInstalled).ToList(), remove: false);
                break;
            case "select":
                foreach (var i in _items) i.IsSelected = card.Available.Contains(i) && !i.IsInstalled;
                UpdateSelectionButton();
                Tabs.SelectedItem = CatalogTab;
                break;
            case "delete" when card.UserPreset is not null:
                UserData.RemovePreset(card.UserPreset);
                BuildSets();
                break;
        }
    }

    private void OnSavePreset(object? sender, RoutedEventArgs e)
    {
        var ids = _items.Where(i => i.IsSelected).Select(i => i.Id).ToList();
        if (ids.Count == 0) return;
        var name = PresetNameBox.Text?.Trim();
        if (string.IsNullOrEmpty(name)) name = (L.IsEnglish ? "Set " : "Набор ") + (UserData.Presets.Count + 1);
        UserData.AddPreset(name, ids);
        PresetNameBox.Text = "";
        BuildSets();
        Log((L.IsEnglish ? "Set saved: " : "Набор сохранён: ") + name);
    }

    // ---------- Обновления ----------

    private async void OnCheckUpdates(object? sender, RoutedEventArgs e)
    {
        CheckUpdatesButton.IsEnabled = false;
        try
        {
            var flatpaks = await Flatpak.ListUpdatesAsync();
            var snaps = await Snap.ListUpdatesAsync();
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var i in _items.Where(i => i.PackageId is not null)) names.TryAdd(i.PackageId!, i.Name);
            string Show(string pkg) => names.TryGetValue(pkg, out var n) ? $"{n} ({pkg})" : pkg;

            var lines = flatpaks.Select(p => "Flathub · " + Show(p)).Concat(snaps.Select(p => "Snap · " + Show(p))).ToList();
            UpdatesText.Text = lines.Count == 0
                ? L.T("Обновлений нет.")
                : (L.IsEnglish ? $"Updates available: {lines.Count}" : $"Есть обновления: {lines.Count}") +
                  Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, lines);
            UpdateAllButton.IsEnabled = lines.Count > 0;
            _pendingFlatpakUpdates = flatpaks.Count > 0;
            _pendingSnapUpdates = snaps.Count > 0;
        }
        catch (Exception ex) { Log(L.T("Ошибка: ") + ex.Message); }
        finally
        {
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    private bool _pendingFlatpakUpdates, _pendingSnapUpdates;

    private async void OnUpdateAll(object? sender, RoutedEventArgs e)
    {
        // Обновление и установка из каталога внахлёст дерутся за блокировку flatpak/snap.
        if (Busy) return;
        Busy = true;
        UpdateAllButton.IsEnabled = false;
        UpdateSelectionButton();
        Log(L.IsEnglish ? "--- Updating" : "--- Обновление");
        try
        {
            var ok = true;
            if (_pendingFlatpakUpdates) ok &= await Flatpak.UpdateAllAsync(Log);
            if (_pendingSnapUpdates) ok &= await Snap.UpdateAllAsync(Log);
            Log(ok ? L.T("Готово.") : L.T("Не получилось, подробности выше."));
        }
        catch (Exception ex) { Log(L.T("Ошибка: ") + ex.Message); }
        finally
        {
            Busy = false;
            UpdateSelectionButton();
        }
        OnCheckUpdates(sender, e);
    }

    // ---------- Настройки ----------

    private void OnLanguageChanged(object? sender, SelectionChangedEventArgs e)
    {
        var lang = LanguageBox.SelectedIndex == 1 ? "en" : "ru";
        if (UserData.Settings.Language == lang || (UserData.Settings.Language is null && (lang == "en") == L.IsEnglish)) return;
        UserData.Settings.Language = lang;
        UserData.SaveSettings();
        LanguageHint.IsVisible = true;
    }

    private async void OnExportProfile(object? sender, RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = $"Ven4Tools-profile-{DateTime.Now:yyyy-MM-dd}.zip",
            FileTypeChoices = new[] { new FilePickerFileType("ZIP") { Patterns = new[] { "*.zip" } } }
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        try
        {
            UserData.ExportProfile(path);
            Log((L.IsEnglish ? "Profile exported: " : "Профиль сохранён: ") + path);
        }
        catch (Exception ex) { Log(L.T("Ошибка: ") + ex.Message); }
    }

    private async void OnImportProfile(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("ZIP") { Patterns = new[] { "*.zip" } } }
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
        try
        {
            var (favs, presets) = UserData.ImportProfile(path);
            foreach (var i in _items) i.IsFavorite = IsFavorite(i);
            BuildSets();
            ApplyFilter();
            Log(L.IsEnglish
                ? $"Profile imported: favorites {favs}, sets {presets}."
                : $"Профиль загружен: избранное {favs}, наборы {presets}.");
        }
        catch (Exception ex) { Log(L.T("Ошибка: ") + ex.Message); }
    }

    // Служебный вывод flatpak при установке («lseek error in child setup», склеивается без переводов строк);
    // на результат не влияет и только забивает журнал
    private const string FlatpakNoise = "lseek error in child setup";

    private void Log(string line)
    {
        if (line.Contains(FlatpakNoise, StringComparison.Ordinal))
        {
            line = line.Replace(FlatpakNoise, "", StringComparison.Ordinal);
            if (line.Trim().Trim('\r', '\n').Length == 0) return;
        }
        Dispatcher.UIThread.Post(() =>
        {
            LogBox.Text += line + Environment.NewLine;
            LogBox.CaretIndex = LogBox.Text.Length;
            // Свёрнутый «Центр активности» показывает последнюю строку
            if (line.Trim().Length > 0) LastLogText.Text = line.Trim();
        });
    }
}
