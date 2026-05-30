using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ven4Tools.Models;
using Ven4Tools.Services;

namespace Ven4Tools.Views.Tabs
{
    public partial class ProfileTab : UserControl
    {
        public ProfileTab()
        {
            InitializeComponent();
            BuildPaletteButtons();
            Loaded += async (_, _) => await RefreshAsync();
            GamificationService.Instance.PointsChanged      += (_, _) => Dispatcher.Invoke(async () => await RefreshAsync());
            GamificationService.Instance.AchievementUnlocked += ShowAchievementToast;
            GamificationService.Instance.MedalUnlocked       += ShowMedalToast;
        }

        // ── Refresh ───────────────────────────────────────────────────────────────

        public async System.Threading.Tasks.Task RefreshAsync()
        {
            var data = await GamificationService.Instance.GetDataAsync();
            var level = GamificationService.Instance.GetLevelInfo(data.TotalPoints);

            // Level card
            txtLevelIcon.Text    = level.Icon;
            txtLevelName.Text    = level.Name;
            txtPoints.Text       = $"{data.TotalPoints} очков";
            txtInstallCount.Text = data.InstallCount.ToString();
            txtUpdateCount.Text  = data.UpdateCount.ToString();
            txtVisitCount.Text   = data.DailyVisitCount.ToString();

            progressLevel.Value = level.Progress * 100;

            if (level.Progress >= 1.0)
                txtNextLevel.Text = "Максимальный уровень достигнут 🌟";
            else
                txtNextLevel.Text = $"До уровня «{level.NextLevelName}»: {level.NextLevelPoints - data.TotalPoints} очков";

            // Medals
            panelMedals.Children.Clear();
            foreach (var medal in GamificationService.AllMedals)
            {
                bool earned = data.UnlockedMedals.Contains(medal.Id);
                panelMedals.Children.Add(BuildMedalCard(medal, earned));
            }

            // Achievements
            int unlocked = data.UnlockedAchievements.Count;
            txtAchievCount.Text = $"{unlocked} / {GamificationService.AllAchievements.Count}";
            panelAchievements.Children.Clear();
            foreach (var ach in GamificationService.AllAchievements)
            {
                bool earned = data.UnlockedAchievements.Contains(ach.Id);
                panelAchievements.Children.Add(BuildAchievementCard(ach, earned));
            }

            // Highlight active palette button
            HighlightActivePalette();

            // Leaderboard (stub — your real points + anonymous placeholders)
            var entries = new List<LeaderboardEntry>
            {
                new() { Rank = "🥇  Вы", Name = UserSession.IsLoggedIn ? UserSession.Name : "Вы", Points = $"{data.TotalPoints} очков" },
                new() { Rank = "2.",  Name = "—",   Points = "???" },
                new() { Rank = "3.",  Name = "—",   Points = "???" },
                new() { Rank = "4.",  Name = "—",   Points = "???" },
                new() { Rank = "5.",  Name = "—",   Points = "???" },
            };
            listLeaderboard.ItemsSource = entries;
        }

        // ── Medals ────────────────────────────────────────────────────────────────

        private UIElement BuildMedalCard(MedalDefinition medal, bool earned)
        {
            var border = new Border
            {
                CornerRadius = new CornerRadius(12),
                Padding      = new Thickness(12, 10, 12, 10),
                Margin       = new Thickness(0, 0, 10, 8),
                Width        = 110,
                Background   = earned
                    ? (Brush)Application.Current.Resources["AccentColor"]
                    : (Brush)Application.Current.Resources["CardBackground"],
                Opacity = earned ? 1.0 : 0.45
            };

            var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            sp.Children.Add(new TextBlock
            {
                Text = medal.Icon, FontSize = 28,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            sp.Children.Add(new TextBlock
            {
                Text = medal.Title, FontSize = 12, FontWeight = FontWeights.Bold,
                Foreground = earned ? Brushes.White : (Brush)Application.Current.Resources["TextPrimary"],
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 0)
            });
            sp.Children.Add(new TextBlock
            {
                Text = earned ? "Получена" : medal.RequirementText,
                FontSize = 10,
                Foreground = earned
                    ? new SolidColorBrush(Color.FromArgb(200, 255, 255, 255))
                    : (Brush)Application.Current.Resources["TextSecondary"],
                HorizontalAlignment = HorizontalAlignment.Center
            });

            border.Child = sp;
            return border;
        }

        // ── Achievements ──────────────────────────────────────────────────────────

        private UIElement BuildAchievementCard(AchievementDefinition ach, bool earned)
        {
            var border = new Border
            {
                Background   = (Brush)Application.Current.Resources["CardBackground"],
                CornerRadius = new CornerRadius(10),
                Padding      = new Thickness(12),
                Margin       = new Thickness(4),
                Width        = 145,
                Height       = 115,
                Opacity      = earned ? 1.0 : 0.4
            };

            var sp = new StackPanel();
            sp.Children.Add(new TextBlock
            {
                Text = earned ? ach.Icon : "🔒",
                FontSize = 26,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 6)
            });
            sp.Children.Add(new TextBlock
            {
                Text = ach.Title, FontSize = 11, FontWeight = FontWeights.Bold,
                Foreground = (Brush)Application.Current.Resources["TextPrimary"],
                HorizontalAlignment = HorizontalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center
            });
            sp.Children.Add(new TextBlock
            {
                Text = ach.Description, FontSize = 10,
                Foreground = (Brush)Application.Current.Resources["TextSecondary"],
                HorizontalAlignment = HorizontalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 3, 0, 0)
            });

            if (earned)
            {
                border.BorderBrush     = (Brush)Application.Current.Resources["AccentColor"];
                border.BorderThickness = new Thickness(2);
            }

            border.Child = sp;
            return border;
        }

        // ── Color palette ─────────────────────────────────────────────────────────

        private void BuildPaletteButtons()
        {
            panelPalette.Children.Clear();
            foreach (var (name, hex) in ThemeService.Palettes)
            {
                var color = (Color)ColorConverter.ConvertFromString(hex);
                var btn = new Button
                {
                    Style      = (Style)FindResource("PaletteBtn"),
                    Background = new SolidColorBrush(color),
                    Tag        = hex,
                    ToolTip    = name
                };
                btn.Click += OnPaletteClick;
                panelPalette.Children.Add(btn);
            }

            // Reset to default
            var reset = new Button
            {
                Content    = "↩",
                Width      = 36, Height = 36,
                Margin     = new Thickness(4),
                Cursor     = System.Windows.Input.Cursors.Hand,
                ToolTip    = "Сбросить (цвет темы)",
                Tag        = ""
            };
            reset.Click += OnPaletteClick;
            panelPalette.Children.Add(reset);
        }

        private void OnPaletteClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            string hex = btn.Tag?.ToString() ?? "";

            ProfileService.Current.AccentColorHex = hex;
            ProfileService.Save();

            ThemeService.Apply(ProfileService.Current.Theme);
            HighlightActivePalette();
            UpdatePaletteLabel(hex);

            _ = GamificationService.Instance.TrackPaletteChangeAsync();
        }

        private void HighlightActivePalette()
        {
            string current = ProfileService.Current.AccentColorHex;
            foreach (UIElement el in panelPalette.Children)
            {
                if (el is Button btn)
                {
                    string tag = btn.Tag?.ToString() ?? "";
                    btn.BorderBrush = tag == current
                        ? Brushes.White
                        : Brushes.Transparent;
                }
            }
            UpdatePaletteLabel(current);
        }

        private void UpdatePaletteLabel(string hex)
        {
            if (string.IsNullOrEmpty(hex))
            {
                txtCurrentPalette.Text = "Используется цвет текущей темы";
                return;
            }
            var match = Array.Find(ThemeService.Palettes, p => p.Hex == hex);
            txtCurrentPalette.Text = match != default
                ? $"Активна схема: {match.Name} ({hex})"
                : $"Активен цвет: {hex}";
        }

        // ── Toasts ────────────────────────────────────────────────────────────────

        private void ShowAchievementToast(AchievementDefinition ach)
        {
            Dispatcher.Invoke(() =>
                MessageBox.Show(
                    $"Достижение получено!\n\n{ach.Icon}  {ach.Title}\n{ach.Description}\n\n+{ach.BonusPoints} очков",
                    "Новое достижение!", MessageBoxButton.OK, MessageBoxImage.Information));
        }

        private void ShowMedalToast(MedalDefinition medal)
        {
            Dispatcher.Invoke(() =>
                MessageBox.Show(
                    $"Медаль получена!\n\n{medal.Icon}  {medal.Title}\n{medal.RequirementText}",
                    "Новая медаль!", MessageBoxButton.OK, MessageBoxImage.Information));
        }

        private sealed class LeaderboardEntry
        {
            public string Rank   { get; set; } = "";
            public string Name   { get; set; } = "";
            public string Points { get; set; } = "";
        }
    }
}
