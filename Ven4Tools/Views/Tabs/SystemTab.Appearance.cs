using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Ven4Tools.Models;
using Ven4Tools.Services;
using Ven4Tools.Shared;

namespace Ven4Tools.Views.Tabs
{
    public partial class SystemTab : UserControl
    {
        private static void SelectComboByTag(ComboBox combo, string value)
        {
            foreach (ComboBoxItem item in combo.Items)
            {
                if (string.Equals(item.Tag?.ToString(), value, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedItem = item;
                    return;
                }
            }
            combo.SelectedIndex = 0;
        }

        private void CmbTheme_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingAppearance || cmbTheme.SelectedItem is not ComboBoxItem item) return;
            ProfileService.Current.Theme = item.Tag?.ToString() ?? "web";
            ProfileService.Save();
            ThemeService.Apply(ProfileService.Current.Theme);
            MotionService.CrossFade((UIElement?)Window.GetWindow(this) ?? this, 220);
        }

        private void CmbLanguage_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingAppearance || cmbLanguage.SelectedItem is not ComboBoxItem item) return;
            ProfileService.Current.Language = item.Tag?.ToString() ?? "auto";
            ProfileService.Save();
            var language = ProfileService.Current.Language;
            if (language == "auto")
                language = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru" ? "ru" : "en";
            LocalizationService.Apply(language);
        }

        private void ChkCompactMode_Click(object sender, RoutedEventArgs e)
        {
            if (_loadingAppearance) return;
            ProfileService.Current.CompactMode = chkCompactMode.IsChecked == true;
            ProfileService.Save();
        }

        private void ChkReduceMotion_Click(object sender, RoutedEventArgs e)
        {
            if (_loadingAppearance) return;
            ProfileService.Current.ReduceMotion = chkReduceMotion.IsChecked == true;
            MotionService.Enabled = !ProfileService.Current.ReduceMotion;
            ProfileService.Save();
        }

        private void ChkMinimizeToTray_Click(object sender, RoutedEventArgs e)
        {
            ProfileService.Current.MinimizeToTray = chkMinimizeToTray.IsChecked == true;
            ProfileService.Save();
        }
    }
}
