using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Ven4Tools.Models;
using Ven4Tools.Services;

namespace Ven4Tools.Views.Tabs
{
    public partial class CatalogTab
    {
        private readonly ObservableCollection<Preset> _presets = new();

        private Preset? _pendingUpdatePreset; // пресет, состав которого обновляется

        private void InitPresets()
        {
            lstPresets.ItemsSource = _presets;
            _ = RefreshPresetsAsync();
        }

        private async Task RefreshPresetsAsync()
        {
            // Сбрасываем режим обновления состава при перезагрузке списка
            _pendingUpdatePreset    = null;
            btnSavePreset.Content   = "💾 Сохранить выбор";
            btnSavePreset.ToolTip   = "Сохранить отмеченные приложения как пресет";

            var list = await PresetService.LoadAsync();
            _presets.Clear();
            foreach (var p in list) _presets.Add(p);

            txtPresetsEmpty.Visibility = _presets.Count == 0
                ? Visibility.Visible : Visibility.Collapsed;
        }

        // ── Сохранить выбор ───────────────────────────────────────────────────────

        private async void BtnSavePreset_Click(object sender, RoutedEventArgs e)
        {
            // Режим обновления состава существующего пресета
            if (_pendingUpdatePreset != null)
            {
                var updating = _pendingUpdatePreset;
                _pendingUpdatePreset = null;
                btnSavePreset.Content = "💾 Сохранить выбор";
                btnSavePreset.ToolTip = "Сохранить отмеченные приложения как пресет";

                var selectedApps = GetSelectedApps();
                if (selectedApps.Count == 0) return;

                var previousApps = updating.Apps;
                updating.Apps = selectedApps;
                btnSavePreset.IsEnabled = false;
                try
                {
                    bool ok = await PresetService.UpdateAsync(updating);
                    if (ok) updating.RaiseAppCountChanged();
                    else    updating.Apps = previousApps;
                    AppLogger.Write(ok
                        ? $"✅ Состав пресета «{updating.Name}» обновлён ({selectedApps.Count} прил.)"
                        : $"❌ Не удалось обновить состав пресета «{updating.Name}»");
                }
                finally { btnSavePreset.IsEnabled = true; }
                return;
            }

            var selected = GetSelectedApps();
            if (selected.Count == 0) return;

            var dlg = new Views.PresetSaveDialog(selected.Count) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() != true) return;

            var preset = new Preset { Name = dlg.PresetName, Description = dlg.PresetDescription, Apps = selected };

            btnSavePreset.IsEnabled = false;
            try
            {
                var saved = await PresetService.SaveAsync(preset);
                if (saved == null) { AppLogger.Write("❌ Не удалось сохранить пресет"); return; }

                _presets.Insert(0, saved);
                txtPresetsEmpty.Visibility = Visibility.Collapsed;
                AppLogger.Write($"✅ Пресет «{saved.Name}» сохранён ({selected.Count} прил.)");
            }
            finally
            {
                btnSavePreset.IsEnabled = true;
            }
        }

        // ── Применить пресет ──────────────────────────────────────────────────────

        private void BtnApplyPreset_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is not Preset preset) return;
            ApplyPreset(preset);
        }

        private void ApplyPreset(Preset preset)
        {
            int applied = 0;
            foreach (var id in preset.Apps)
            {
                if (appCheckBoxes.TryGetValue(id, out var cb) && cb.IsEnabled)
                {
                    cb.IsChecked = true;
                    applied++;
                }
            }
            UpdateInstallButton();
            AppLogger.Write($"📋 Пресет «{preset.Name}» применён: {applied} из {preset.Apps.Count} приложений отмечено");
        }

        // ── Переименовать (имя / описание) ────────────────────────────────────────

        private async void BtnRenamePreset_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is not Preset preset) return;
            var btn = (Button)sender;

            var dlg = new Views.PresetSaveDialog(preset.Name, preset.Description)
                { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() != true) return;

            string oldName        = preset.Name;
            string oldDescription = preset.Description;
            preset.Name        = dlg.PresetName;
            preset.Description = dlg.PresetDescription;

            btn.IsEnabled = false;
            try
            {
                bool ok = await PresetService.UpdateAsync(preset);
                if (ok)
                    preset.RaiseNameChanged();
                else
                {
                    preset.Name        = oldName;
                    preset.Description = oldDescription;
                }
                AppLogger.Write(ok
                    ? $"✅ Пресет переименован: «{preset.Name}»"
                    : $"❌ Не удалось переименовать пресет «{oldName}»");
            }
            finally { btn.IsEnabled = true; }
        }

        // ── Обновить состав (применить + перейти в режим обновления) ──────────────

        private void BtnUpdateAppsPreset_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is not Preset preset) return;

            ApplyPreset(preset);
            _pendingUpdatePreset    = preset;
            btnSavePreset.Content   = $"↻ Обновить «{preset.Name}»";
            btnSavePreset.IsEnabled = true;
            btnSavePreset.ToolTip   = "Сохранить текущий выбор как новый состав пресета";
        }

        // ── Удалить пресет ────────────────────────────────────────────────────────

        private async void BtnDeletePreset_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is not Preset preset) return;

            var r = MessageBox.Show($"Удалить пресет «{preset.Name}»?",
                "Пресеты", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;

            if (_pendingUpdatePreset == preset)
            {
                _pendingUpdatePreset  = null;
                btnSavePreset.Content  = "💾 Сохранить выбор";
                btnSavePreset.ToolTip  = "Сохранить отмеченные приложения как пресет";
            }

            await PresetService.DeleteAsync(preset);

            _presets.Remove(preset);
            txtPresetsEmpty.Visibility = _presets.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            AppLogger.Write($"🗑️ Пресет «{preset.Name}» удалён");
        }
    }
}
