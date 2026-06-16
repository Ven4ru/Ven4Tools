using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Ven4Tools.Models;
using Ven4Tools.Services;

namespace Ven4Tools.Views
{
    public partial class ProfileWindow : Window
    {
        private bool _langChanged = false;
        private string _selectedAccentHex = "";

        public ProfileWindow()
        {
            InitializeComponent();
            LoadFromProfile();
            BuildAccentSwatches();
        }

        private void LoadFromProfile()
        {
            var p = ProfileService.Current;

            // Catalog mode
            rbBasic.IsChecked    = p.CatalogMode == "basic";
            rbExtended.IsChecked = p.CatalogMode == "extended";
            rbFull.IsChecked     = p.CatalogMode == "full" || string.IsNullOrEmpty(p.CatalogMode);

            // Sort
            cmbSort.SelectedIndex = p.DefaultSort switch
            {
                "category" => 1,
                _          => 0
            };

            chkHideInstalled.IsChecked = p.HideInstalled;
            _selectedAccentHex = p.AccentColorHex;

            // UI
            rbDark.IsChecked  = p.Theme == "dark" || p.Theme == "teal" || (p.Theme != "light" && p.Theme != "web");
            rbLight.IsChecked = p.Theme == "light";
            rbWeb.IsChecked   = p.Theme == "web";

            cmbLang.SelectedIndex = p.Language switch
            {
                "ru" => 1,
                "en" => 2,
                _    => 0
            };

            chkCompact.IsChecked      = p.CompactMode;

            // Install
            chkSilent.IsChecked   = p.SilentInstall;
            txtInstallFolder.Text  = p.DefaultInstallFolder;

            // Notifications
            chkNotifUpdates.IsChecked = p.NotifyAppUpdates;
            chkNotifNew.IsChecked     = p.NotifyNewApps;

            // Privacy
            chkSyncFavorites.IsChecked  = p.SyncFavorites;
            chkHistory.IsChecked        = p.SaveInstallHistory;
            chkStats.IsChecked          = p.AnonymousStats;
            chkNoLocalStorage.IsChecked = p.NoLocalStorage;
            UpdateSwatchHighlights();
        }

        private void RbTheme_Checked(object sender, RoutedEventArgs e)
        {
            if (rbWeb?.IsChecked == true)        ThemeService.ApplyWeb();
            else if (rbLight?.IsChecked == true) ThemeService.ApplyDark(false);
            else                                 ThemeService.ApplyTeal();
        }

        private void BtnBrowseFolder_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Папка установки по умолчанию"
            };
            if (dlg.ShowDialog() == true)
                txtInstallFolder.Text = dlg.FolderName;
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            var p = ProfileService.Current;

            p.CatalogMode = rbBasic.IsChecked == true ? "basic"
                          : rbExtended.IsChecked == true ? "extended"
                          : "full";

            p.DefaultSort = cmbSort.SelectedIndex switch
            {
                1 => "category",
                _ => "alpha"
            };

            p.HideInstalled  = chkHideInstalled.IsChecked == true;
            p.AccentColorHex = _selectedAccentHex;
            p.Theme          = rbWeb.IsChecked == true ? "web" : (rbLight.IsChecked == true ? "light" : "teal");
            p.CompactMode     = chkCompact.IsChecked == true;
            p.SilentInstall   = chkSilent.IsChecked == true;
            p.DefaultInstallFolder = txtInstallFolder.Text.Trim();
            p.NotifyAppUpdates = chkNotifUpdates.IsChecked == true;
            p.NotifyNewApps    = chkNotifNew.IsChecked == true;
            p.SyncFavorites      = chkSyncFavorites.IsChecked == true;
            p.SaveInstallHistory = chkHistory.IsChecked == true;
            p.AnonymousStats     = chkStats.IsChecked == true;
            p.NoLocalStorage     = chkNoLocalStorage.IsChecked == true;

            // Language
            string newLang = cmbLang.SelectedIndex switch { 1 => "ru", 2 => "en", _ => "auto" };
            if (newLang != p.Language)
            {
                p.Language = newLang;
                _langChanged = true;
                LocalizationService.Apply(newLang == "auto"
                    ? (System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru" ? "ru" : "en")
                    : newLang);
            }

            ProfileService.Save();

            if (_langChanged)
                System.Windows.MessageBox.Show(
                    LocalizationService.T("prof_lang_restart"),
                    LocalizationService.T("prof_title"),
                    MessageBoxButton.OK, MessageBoxImage.Information);

            DialogResult = true;
        }

        private void BtnDeleteLocalData_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                LocalizationService.T("prof_privacy_delete_confirm"),
                LocalizationService.T("prof_privacy_delete_title"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes) return;

            string appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ven4Tools");

            DeleteIfExists(Path.Combine(appData, "session.json"));
            DeleteIfExists(Path.Combine(appData, "apps.json"));
            DeleteIfExists(Path.Combine(appData, "apps.json.selection"));
            DeleteIfExists(Path.Combine(appData, "catalog_cache.json"));

            UserSession.Logout();

            MessageBox.Show(
                LocalizationService.T("prof_privacy_delete_done"),
                LocalizationService.T("prof_privacy_delete_title"),
                MessageBoxButton.OK, MessageBoxImage.Information);

            DialogResult = true;
        }

        private static void DeleteIfExists(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            // Restore theme if it was changed live without saving
            ThemeService.Apply(ProfileService.Current.Theme);
            DialogResult = false;
        }

        private void BtnReset_Click(object sender, RoutedEventArgs e)
        {
            var result = System.Windows.MessageBox.Show(
                "Сбросить все настройки профиля до значений по умолчанию?",
                LocalizationService.T("prof_title"),
                MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                ProfileService.Reset();
                ThemeService.Apply(ProfileService.Current.Theme);
                LocalizationService.Init();
                LoadFromProfile();
            }
        }

        private void BuildAccentSwatches()
        {
            foreach (var (name, hex) in ThemeService.Palettes)
            {
                var border = new Border
                {
                    Width = 26, Height = 26,
                    Margin = new Thickness(0, 0, 6, 4),
                    CornerRadius = new CornerRadius(4),
                    Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),
                    BorderThickness = new Thickness(2),
                    Cursor = Cursors.Hand,
                    Tag = hex,
                    ToolTip = name
                };
                border.MouseLeftButtonUp += AccentSwatch_Click;
                wpAccentColors.Children.Add(border);
            }
            UpdateSwatchHighlights();
        }

        private void UpdateSwatchHighlights()
        {
            foreach (Border b in wpAccentColors.Children.OfType<Border>())
            {
                bool selected = !string.IsNullOrEmpty(_selectedAccentHex) && b.Tag?.ToString() == _selectedAccentHex;
                b.BorderBrush = selected ? Brushes.White : Brushes.Transparent;
            }
        }

        private void AccentSwatch_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border b && b.Tag is string hex)
            {
                _selectedAccentHex = _selectedAccentHex == hex ? "" : hex;
                if (string.IsNullOrEmpty(_selectedAccentHex))
                    ThemeService.Apply(ProfileService.Current.Theme);
                else
                    ThemeService.ApplyAccent(_selectedAccentHex);
                UpdateSwatchHighlights();
            }
        }

        private void BtnClearAccent_Click(object sender, RoutedEventArgs e)
        {
            _selectedAccentHex = "";
            ThemeService.Apply(ProfileService.Current.Theme);
            UpdateSwatchHighlights();
        }
    }
}
