using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Ven4Tools.Services;

namespace Ven4Tools.Views.Tabs
{
    /// <summary>
    /// Раздел «Персонализация»: оформление Windows в стиле Венчика. Вся работа с системой —
    /// в <see cref="PersonalizationService"/>, здесь только карточки и кнопки.
    /// </summary>
    public partial class PersonalizationTab : UserControl
    {
        public sealed class PartItem
        {
            public PersonalizationPart Part { get; init; }
            public string Title { get; init; } = "";
            public string Description { get; init; } = "";
            public ImageSource? Preview { get; init; }
            public Visibility PreviewVisibility => Preview == null ? Visibility.Collapsed : Visibility.Visible;
            public bool IsAvailable { get; init; }
            public bool IsApplied { get; init; }
            public bool CanApply => IsAvailable && !IsApplied;
            // Текст карточек переводит сам TextBlock при показе — Tr() здесь не нужен.
            public string Status => !IsAvailable ? "Файлы оформления не найдены"
                : IsApplied ? "✓ Применено" : "Не применено";
            public Brush StatusBrush => (Brush)Application.Current.FindResource(
                !IsAvailable ? "StatusDanger" : IsApplied ? "StatusSuccess" : "TextSecondary");
        }

        private static readonly (PersonalizationPart Part, string Title, string Description)[] Parts =
        {
            (PersonalizationPart.Cursors, "Курсоры-лапы", "Указатель — стрелка в полоску, как шерсть Венчика, на ссылках — лапка с подушечками, а пока Windows занята, Венчик бежит на месте."),
            (PersonalizationPart.Wallpaper, "Обои", "Венчик ночью в компьютерном классе. Левая половина тёмная — значки на рабочем столе хорошо видны."),
            (PersonalizationPart.LockScreen, "Экран блокировки", "Спящий Венчик на экране входа в Windows. Пока картинка включена, сменить экран блокировки в параметрах Windows нельзя."),
            (PersonalizationPart.Sounds, "Звуки Windows", "Уведомления мяукают, ошибки и подключение устройств звучат мягкими сигналами."),
            (PersonalizationPart.Screensaver, "Заставка", "Когда компьютер простаивает, по экрану бегает Венчик. Если заставка была выключена, она включится через 10 минут простоя."),
        };

        private readonly PersonalizationService _service = PersonalizationService.Default;

        public PersonalizationTab()
        {
            InitializeComponent();
            Refresh();
            RefreshTaskbar();
            // Цвета статусов, рамок наборов и плашки читаемости — разовые снимки ресурса
            // (FindResource), сами о смене темы клиента они не узнают. Отписки нет:
            // экземпляр вкладки один на весь сеанс (как у ViewModel других вкладок).
            ThemeService.ThemeChanged += () => Dispatcher.Invoke(() => { Refresh(); RefreshTaskbarSelection(); });
        }

        // ── Панель задач и Пуск (блок 1, дизайн 04.11.2026) ─────────────────

        public sealed class Choice
        {
            public int Value { get; init; }
            public string Label { get; init; } = "";
        }

        public sealed class TaskbarItem
        {
            public TaskbarSetting Setting { get; init; } = null!;
            public string Group => Setting.Group;
            public string Title => Setting.Title;
            public string Description => Setting.Description;
            public List<Choice> Choices { get; init; } = new();
            public Choice? Selected { get; set; }
            public int CurrentValue { get; init; }
            public bool Changed { get; init; }
            public string Status => Changed ? "✓ Изменено Ven4Tools — можно вернуть" : "Как в Windows сейчас";
        }

        public sealed class ThemeItem
        {
            public TaskbarTheme Theme { get; init; } = null!;
            public string Title => Theme.Title;
            public string Tip { get; init; } = "";
            public Brush BarBrush { get; init; } = Brushes.Gray;
            public Brush AccentBrush { get; init; } = Brushes.White;
            public bool Selected { get; init; }
            public Visibility RingVisibility => Selected ? Visibility.Visible : Visibility.Hidden;
        }

        public sealed class PresetItem
        {
            public string Title { get; init; } = "";
            public string Description { get; init; } = "";
            public Dictionary<string, int> Values { get; init; } = new();
            /// <summary>Набор только про цвет («Тёмная с акцентом») — выбирает тему, а не настройки.</summary>
            public bool ThemeOnly { get; init; }
            public ImageSource? Wallpaper { get; init; }
            public TaskbarTheme Theme { get; init; } = TaskbarThemeService.Themes[2];
            public bool Centered { get; init; }
            public int Search { get; init; }
            public bool TaskView { get; init; }
            public bool Widgets { get; init; }
            public bool Labels { get; init; }
            public bool StartMenu { get; init; }
            public bool Ears { get; init; }
            public bool SearchLight { get; init; } = true;
            public bool Selected { get; init; }
            public Brush BorderBrush => Selected ? (Brush)Application.Current.FindResource("AccentColor") : (Brush)Application.Current.FindResource("BorderBrush");
            public Thickness BorderThick => new(Selected ? 2 : 1);
        }

        private readonly TaskbarStartService _taskbar = TaskbarStartService.Default;
        private readonly TaskbarThemeService _themes = TaskbarThemeService.Default;
        private TaskbarTheme? _selTheme;
        private TaskbarTheme? _custom;
        private bool _customDarkened;
        private PresetItem? _selPreset;
        private ImageSource? _wallpaper;
        private bool _loading;

        private static Brush Br(string hex) { var (r, g, b) = TaskbarThemeService.Rgb(hex); var br = new SolidColorBrush(Color.FromRgb(r, g, b)); br.Freeze(); return br; }

        private ImageSource? UserWallpaper()
        {
            try
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
                var path = k?.GetValue("Wallpaper") as string;
                if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
                    path = System.IO.Path.Combine(AppContext.BaseDirectory, "Personalization", "Venchik", PersonalizationService.WallpaperFile);
                return System.IO.File.Exists(path) ? LoadPreview(path, 1100) : null;
            }
            catch (Exception ex) { AppLogger.Write("⚠️ Витрина панели: обои не прочитаны — " + ex.Message); return null; }
        }

        private int CurrentValue(string id)
        {
            var s = TaskbarStartService.All.FirstOrDefault(x => x.Id == id);
            return s == null ? 0 : _taskbar.Current(s);
        }

        /// <summary>Как будет выглядеть панель: текущие настройки Windows поверх — значения набора.</summary>
        private (bool Centered, int Search, bool TaskView, bool Widgets, bool Labels, bool StartMenu) Layout(PresetItem? p)
        {
            int V(string id) => p != null && p.Values.TryGetValue(id, out var v) ? v : CurrentValue(id);
            bool win11 = _taskbar.IsWindows11;
            return (win11 && V("Align") == 1,
                    win11 ? V("Search11") : V("Search10"),
                    V("TaskView") == 1,
                    win11 && V("Widgets") == 1,
                    V("Combine") != 0,
                    p?.Values.ContainsKey("StartLayout") == true);
        }

        private void RefreshTaskbar()
        {
            _loading = true;
            try
            {
                _wallpaper ??= UserWallpaper();
                var st = _themes.Current;
                _selTheme ??= _themes.AppliedTheme ?? TaskbarThemeService.Themes[0];
                if (_selTheme.Id == "custom") _custom = _selTheme;

                // Цвет теперь задают темы — пункты группы «Цвет» в тонкой настройке не дублируем.
                var fine = _taskbar.Available.Where(s => s.Group != "Цвет").ToList();
                lstTaskbar.ItemsSource = fine.Select(s =>
                {
                    var choices = s.Choices.Select(c => new Choice { Value = c.Value, Label = Tr(c.Label) }).ToList();
                    var current = _taskbar.Current(s);
                    return new TaskbarItem
                    {
                        Setting = s,
                        Choices = choices,
                        CurrentValue = current,
                        Selected = choices.FirstOrDefault(c => c.Value == current) ?? choices.FirstOrDefault(),
                        Changed = _taskbar.IsChanged(s),
                    };
                }).ToList();
                txtFineHeader.Text = string.Format(Tr("Тонкая настройка · {0} пунктов"), fine.Count);

                lstThemes.ItemsSource = TaskbarThemeService.Themes.Select(t => new ThemeItem
                {
                    Theme = t,
                    BarBrush = Br(t.Prevalence ? t.Bar : (t.Dark ? "#202020" : "#EEEEEE")),
                    AccentBrush = Br(t.Inner),
                    Selected = _selTheme.Id == t.Id,
                    Tip = string.Format(Tr("Читаемость подписей {0:0.0}:1"), TaskbarThemeService.Readability(t)),
                }).ToList();

                chkEars.IsEnabled = TaskbarThemeService.EarsAvailable;
                // Всегда по сохранённому состоянию: раньше при применённой теме без ушек
                // флажок оставался «включён» из разметки, и следующее «Применить»
                // возвращало ушки, которые человек выключил.
                chkEars.IsChecked = TaskbarThemeService.EarsAvailable && (st.Ears || st.ThemeId == null);
                // Так же и прозрачность: тема применена без неё — флажок не должен
                // включать её обратно при следующем «Применить».
                // Только для темы, которая сама прозрачность допускает: иначе выключенная
                // темой прозрачность сняла бы флажок и для остальных тем.
                if (_themes.AppliedTheme is { Transparency: true }) chkTransparency.IsChecked = SystemTransparency();
                cmbEdge.IsEnabled = _themes.CanMoveLeft;
                var cur = _themes.CanMoveLeft ? _themes.Edge : TaskbarThemeService.EdgeBottom;
                cmbEdge.SelectedItem = cmbEdge.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == cur.ToString()) ?? cmbEdge.Items[0];
                cmbEdge.ToolTip = _themes.CanMoveLeft
                    ? Tr("Панель задач у любого края экрана — родная настройка Windows 10. После применения проводник перезапустится.")
                    : Tr("В Windows 11 родную панель задач нельзя переставить. Панель сверху, слева или справа — в своей панели Ven4Bar.");

                RefreshPresets();
                // Испорченный файл отката: «Применить» отказывает, а «Вернуть» доступно —
                // оно честно скажет, что вернуть нечего, и снимет отказ.
                var broken = TaskbarBrokenMessage();
                btnTaskbarRevert.IsEnabled = _taskbar.HasUndo || _themes.HasUndo || broken != null;
                var applied = _themes.AppliedTheme;
                txtTaskbarStatus.Text = broken != null ? "⚠️ " + broken
                    : applied != null ? string.Format(Tr("✓ Применена тема «{0}»"), Tr(applied.Title)) : "";
            }
            catch (Exception ex)
            {
                AppLogger.Write("❌ Панель задач: настройки не прочитаны — " + ex.Message);
            }
            finally { _loading = false; }
            UpdatePreview();
        }

        private void RefreshPresets()
        {
            var theme = PreviewTheme();
            lstPresets.ItemsSource = TaskbarStartService.Presets.Select(p =>
            {
                bool themeOnly = p.Values.Keys.All(k => k is "DarkSystem" or "AccentBar" or "Transparency");
                var item = new PresetItem { Title = p.Title, Description = p.Description, Values = p.Values, ThemeOnly = themeOnly };
                var l = Layout(themeOnly ? null : item);
                return new PresetItem
                {
                    Title = p.Title,
                    Description = themeOnly ? "Тёмные панель и Пуск в цвете темы — всегда читаются." : p.Description,
                    Values = p.Values,
                    ThemeOnly = themeOnly,
                    Wallpaper = _wallpaper,
                    Theme = themeOnly ? TaskbarThemeService.Themes.First(t => t.Id == "violet") : theme,
                    Centered = l.Centered, Search = l.Search, TaskView = l.TaskView, Widgets = l.Widgets, Labels = l.Labels, StartMenu = l.StartMenu,
                    Ears = chkEars.IsChecked == true,
                    SearchLight = AppsLight(),
                    Selected = _selPreset?.Title == p.Title,
                };
            }).ToList();
        }

        private static bool AppsLight()
        {
            try
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return k?.GetValue("AppsUseLightTheme") is not int v || v != 0;
            }
            catch { return true; }
        }

        private static bool SystemTransparency()
        {
            try
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return k?.GetValue("EnableTransparency") is not int v || v != 0;
            }
            catch { return true; }
        }

        private TaskbarTheme PreviewTheme()
        {
            var t = _selTheme ?? TaskbarThemeService.Themes[0];
            return t with { Transparency = t.Transparency && chkTransparency.IsChecked == true };
        }

        private void UpdatePreview()
        {
            if (_loading) return;
            var t = PreviewTheme();
            var l = Layout(_selPreset is { ThemeOnly: false } ? _selPreset : null);
            preview.Wallpaper = _wallpaper;
            preview.Theme = t;
            preview.Transparency = t.Transparency;
            preview.Centered = l.Centered; preview.Search = l.Search; preview.TaskView = l.TaskView;
            preview.Widgets = l.Widgets; preview.Labels = l.Labels; preview.StartMenu = l.StartMenu;
            preview.Ears = chkEars.IsChecked == true;
            preview.SearchLight = AppsLight();
            preview.Edge = _themes.CanMoveLeft ? SelectedEdge() : TaskbarThemeService.EdgeBottom;

            var r = TaskbarThemeService.Readability(t);
            txtReadability.Text = string.Format(Tr("Читаемость подписей: {0:0.0}:1 · {1}"), r, Tr(r >= 7 ? "отлично" : r >= 4.5 ? "хорошо" : "плохо"));
            pillReadability.Background = (Brush)Application.Current.FindResource(r >= 4.5 ? "AccentSoftBackground" : "StatusDanger");
            // На красной плашке — парный ей цвет надписи: TextPrimary тёмных тем (почти белый)
            // на светло-красном StatusDanger читался плохо.
            txtReadability.Foreground = (Brush)Application.Current.FindResource(r >= 4.5 ? "AccentColor" : "StatusDangerForeground");
            txtCustomNote.Visibility = _selTheme?.Id == "custom" && _customDarkened ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Theme_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not ThemeItem item) return;
            _selTheme = item.Theme;
            if (_selPreset is { ThemeOnly: true }) _selPreset = null;
            RefreshTaskbarSelection();
        }

        private void CustomColor_Click(object sender, RoutedEventArgs e)
        {
            using var dlg = new System.Windows.Forms.ColorDialog { FullOpen = true, AnyColor = true };
            if (_custom != null) { var (r, g, b) = TaskbarThemeService.Rgb(_custom.Accent); dlg.Color = System.Drawing.Color.FromArgb(r, g, b); }
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            var hex = $"#{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}";
            _custom = TaskbarThemeService.FromCustom(hex, true, out _customDarkened);
            _selTheme = _custom;
            RefreshTaskbarSelection();
        }

        private void RefreshTaskbarSelection()
        {
            if (lstThemes.ItemsSource is IEnumerable<ThemeItem> themes)
                lstThemes.ItemsSource = themes.Select(t => new ThemeItem { Theme = t.Theme, BarBrush = t.BarBrush, AccentBrush = t.AccentBrush, Tip = t.Tip, Selected = _selTheme?.Id == t.Theme.Id }).ToList();
            RefreshPresets();
            UpdatePreview();
        }

        private int SelectedEdge() => cmbEdge.SelectedItem is ComboBoxItem { Tag: string t } && int.TryParse(t, out var v) ? v : TaskbarThemeService.EdgeBottom;

        private void TaskbarEdge_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || !IsLoaded) return;
            UpdatePreview();
        }

        private void TaskbarOption_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading || !IsLoaded) return;
            RefreshPresets();
            UpdatePreview();
        }

        private void Preset_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not PresetItem p) return;
            if (_selPreset?.Title == p.Title) _selPreset = null;     // повторное нажатие снимает выбор
            else
            {
                _selPreset = p;
                if (p.ThemeOnly) _selTheme = p.Theme;
            }
            RefreshTaskbarSelection();
        }

        /// <summary>Отказ «Применить» раздела панели задач из-за испорченного файла отката; null — отказа нет.</summary>
        private string? TaskbarBrokenMessage() =>
            (_themes.BrokenBackup ?? _taskbar.BrokenBackup) is { } bad ? UndoFileGuard.BrokenMessage(bad) : null;

        /// <summary>Файл отката испорчен — в журнал и в строку состояния, «Применить» не идёт.</summary>
        private bool RefuseIfTaskbarUndoBroken()
        {
            if (TaskbarBrokenMessage() is not { } broken) return false;
            AppLogger.Write("⚠️ Панель задач: " + broken);
            RefreshTaskbar();   // строка состояния показывает отказ
            return true;
        }

        private async void TaskbarApply_Click(object sender, RoutedEventArgs e)
        {
            if (RefuseIfTaskbarUndoBroken()) return;
            var theme = PreviewTheme();
            var preset = _selPreset is { ThemeOnly: false } ? _selPreset.Values : null;
            bool ears = chkEars.IsChecked == true && TaskbarThemeService.EarsAvailable;
            int edge = SelectedEdge();
            IsEnabled = false;
            bool restart = false;
            var blocked = new List<string>();
            try
            {
                await Task.Run(() =>
                {
                    _themes.ApplyTheme(theme);
                    if (preset != null)
                    {
                        var r = _taskbar.Apply(preset);
                        blocked.AddRange(r.Blocked);
                        restart |= r.NeedsExplorerRestart;
                    }
                    if (ears) _themes.EnableEars(theme); else _themes.DisableEars();
                    if (_themes.CanMoveLeft && edge != _themes.Edge && _themes.SetEdge(edge)) restart = true;
                });
            }
            catch (UndoFileBrokenException)
            {
                // Уже в журнале; строку состояния обновит RefreshTaskbar ниже.
            }
            catch (Exception ex)
            {
                AppLogger.Write("❌ Панель задач: " + ex.Message);
                MessageBox.Show("Не получилось изменить настройки. Подробности — в журнале.", "Панель задач и Пуск", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                IsEnabled = true;
                _selPreset = null;
                RefreshTaskbar();
            }
            await AfterTaskbarChange(blocked, restart);
        }

        private async Task AfterTaskbarChange(List<string> blocked, bool restart)
        {
            if (blocked.Count > 0)
            {
                MessageBox.Show(string.Format("Windows не разрешает менять эти настройки программам: {0}. Сейчас откроется окно «Параметры» — там это можно сделать вручную.",
                    string.Join(", ", blocked.Select(Tr))), "Панель задач и Пуск", MessageBoxButton.OK, MessageBoxImage.Information);
                OpenTaskbarSettings_Click(this, new RoutedEventArgs());
            }
            if (restart &&
                MessageBox.Show("Часть настроек появится после перезапуска проводника. Открытые окна папок закроются. Перезапустить сейчас?",
                    "Панель задач и Пуск", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                // Вызывается из async void-обработчиков кнопок: исключение отсюда уронило бы программу.
                try { await Task.Run(_taskbar.RestartExplorer); }
                catch (Exception ex) { AppLogger.Write("❌ Перезапуск проводника: " + ex.Message); }
            }
        }

        private async void TaskbarFineApply_Click(object sender, RoutedEventArgs e)
        {
            if (lstTaskbar.ItemsSource is not IEnumerable<TaskbarItem> items) return;
            var values = items.Where(i => i.Selected != null && i.Selected.Value != i.CurrentValue)
                .ToDictionary(i => i.Setting.Id, i => i.Selected!.Value);
            if (values.Count == 0) return;
            if (_taskbar.BrokenBackup is { } bad)
            {
                AppLogger.Write("⚠️ Панель задач: " + UndoFileGuard.BrokenMessage(bad));
                RefreshTaskbar();
                return;
            }
            IsEnabled = false;
            TaskbarStartService.ApplyResult? result = null;
            try { result = await Task.Run(() => _taskbar.Apply(values)); }
            catch (UndoFileBrokenException) { /* уже в журнале; состояние — в RefreshTaskbar */ }
            catch (Exception ex)
            {
                AppLogger.Write("❌ Панель задач: " + ex.Message);
                MessageBox.Show("Не получилось изменить настройки. Подробности — в журнале.", "Панель задач и Пуск", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally { IsEnabled = true; RefreshTaskbar(); }
            if (result != null) await AfterTaskbarChange(result.Blocked, result.NeedsExplorerRestart);
        }

        private async void TaskbarRevert_Click(object sender, RoutedEventArgs e)
        {
            IsEnabled = false;
            bool restart = false;
            var blocked = new List<string>();
            var messages = new List<string>();
            try
            {
                await Task.Run(() =>
                {
                    // Сначала тема (её «как было» снято раньше настроек), потом остальные настройки.
                    restart |= _themes.RevertAll(out var themeMessage);
                    var r = _taskbar.RevertAll();
                    blocked.AddRange(r.Blocked);
                    restart |= r.NeedsExplorerRestart;
                    // Файл отката был испорчен — «вернуть нечего», сказать об этом честно.
                    if (themeMessage != null) messages.Add(themeMessage);
                    if (r.Message != null) messages.Add(r.Message);
                });
            }
            catch (Exception ex)
            {
                AppLogger.Write("❌ Панель задач: откат — " + ex.Message);
                MessageBox.Show("Не получилось изменить настройки. Подробности — в журнале.", "Панель задач и Пуск", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                IsEnabled = true;
                _selTheme = null; _selPreset = null;
                RefreshTaskbar();
                if (messages.Count > 0) txtTaskbarStatus.Text = "↩ " + string.Join(" ", messages);
            }
            await AfterTaskbarChange(blocked, restart);
        }

        private async void RestartExplorer_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("Перезапустить проводник? Панель задач на пару секунд пропадёт, открытые окна папок закроются.",
                    "Панель задач и Пуск", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            IsEnabled = false;
            try { await Task.Run(_taskbar.RestartExplorer); }
            catch (Exception ex) { AppLogger.Write("❌ Перезапуск проводника: " + ex.Message); }
            finally { IsEnabled = true; RefreshTaskbar(); }
        }

        private void OpenTaskbarSettings_Click(object sender, RoutedEventArgs e)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:taskbar") { UseShellExecute = true }); }
            catch (Exception ex) { AppLogger.Write("⚠️ Не открылись параметры панели задач — " + ex.Message); }
        }

        private void Refresh()
        {
            // Зовётся из конструктора и из finally обработчиков: сбой чтения состояния
            // не должен ронять создание вкладки или программу.
            try
            {
                var items = Parts.Select(p => new PartItem
                {
                    Part = p.Part,
                    Title = p.Title,
                    Description = p.Description,
                    Preview = LoadPreview(_service.PreviewPath(p.Part)),
                    IsAvailable = _service.IsAvailable(p.Part),
                    IsApplied = _service.IsApplied(p.Part),
                }).ToList();
                lstParts.ItemsSource = items;
                btnApplyAll.IsEnabled = items.Any(i => i.CanApply);
                // Испорченный файл отката: «Вернуть всё» доступно — скажет, что вернуть нечего, и снимет отказ.
                btnRevertAll.IsEnabled = items.Any(i => i.IsApplied) || _service.BrokenBackup != null;
            }
            catch (Exception ex)
            {
                AppLogger.Write("❌ Персонализация: состояние не прочитано — " + ex.Message);
            }
        }

        private static ImageSource? LoadPreview(string? path, int width = 640)
        {
            if (path == null) return null;
            try
            {
                // Без OnLoad файл остаётся открытым, и следующее «Применить» не сможет его скопировать.
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = width;
                image.UriSource = new Uri(path);
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch (Exception ex)
            {
                AppLogger.Write("⚠️ Персонализация: не открыта картинка — " + ex.Message);
                return null;
            }
        }

        private async Task RunAsync(IEnumerable<PersonalizationPart> parts, bool apply)
        {
            // Испорченный файл отката: «Применить» отказывает с понятным сообщением (оно уже в журнале).
            if (apply && _service.BrokenBackup is { } bad)
            {
                var message = UndoFileGuard.BrokenMessage(bad);
                AppLogger.Write("⚠️ Персонализация: " + message);
                MessageBox.Show(message, "Персонализация", MessageBoxButton.OK, MessageBoxImage.Warning);
                Refresh();
                return;
            }
            IsEnabled = false;
            var failed = new List<string>();
            string? brokenMessage = null;
            try
            {
                foreach (var part in parts)
                {
                    try
                    {
                        await Task.Run(() => { if (apply) _service.Apply(part); else _service.Revert(part); });
                    }
                    catch (UndoFileBrokenException ex)
                    {
                        brokenMessage = ex.Message;
                        break;
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Write($"❌ Персонализация: «{part}» — {ex.Message}");
                        failed.Add(Tr(Parts.First(p => p.Part == part).Title));
                    }
                }
            }
            finally
            {
                IsEnabled = true;
                Refresh();
            }
            if (brokenMessage != null)
                MessageBox.Show(brokenMessage, "Персонализация", MessageBoxButton.OK, MessageBoxImage.Warning);
            if (failed.Count > 0)
                MessageBox.Show(string.Format("Не получилось: {0}. Подробности — в журнале.", string.Join(", ", failed)),
                    "Персонализация", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private async void Apply_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is PartItem item) await RunAsync(new[] { item.Part }, apply: true);
        }

        private async void Revert_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is PartItem item) await RunAsync(new[] { item.Part }, apply: false);
        }

        private async void ApplyAll_Click(object sender, RoutedEventArgs e) =>
            await RunAsync(Parts.Select(p => p.Part).Where(p => _service.IsAvailable(p) && !_service.IsApplied(p)), apply: true);

        private async void RevertAll_Click(object sender, RoutedEventArgs e)
        {
            await RunAsync(Parts.Select(p => p.Part).Where(_service.IsApplied), apply: false);
            // Файл отката был испорчен: вернуть по нему нечего — говорим честно и снимаем отказ
            // «Применить» (отложенный файл переименовывается, а не удаляется).
            string? message = null;
            try { message = _service.AcknowledgeBroken(); }
            catch (Exception ex) { AppLogger.Write("⚠️ Персонализация: " + ex.Message); }
            if (message != null)
            {
                Refresh();
                MessageBox.Show(message, "Персонализация", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
    }
}
