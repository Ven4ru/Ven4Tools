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

        private void InitPresets()
        {
            lstPresets.ItemsSource = _presets;
            _ = RefreshPresetsAsync();
            // Подписка на UserSession.Changed вынесена в общий блок Loaded/Unloaded
            // (CatalogTab.xaml.cs), чтобы не было утечки через анонимную лямбду.
        }

        // Обновление списка пресетов при входе/выходе пользователя
        private void OnUserSessionChangedPresets() =>
            _ = Dispatcher.InvokeAsync(async () =>
            {
                try { await RefreshPresetsAsync(); }
                catch (Exception ex) { AppLogger.Write(ex.Message); }
            });

        private async Task RefreshPresetsAsync()
        {
            int? userId = UserSession.IsLoggedIn ? UserSession.UserId : (int?)null;

            txtPresetsSyncHint.Visibility = userId.HasValue
                ? Visibility.Collapsed : Visibility.Visible;

            var list = await PresetService.LoadAsync(userId);
            _presets.Clear();
            foreach (var p in list) _presets.Add(p);

            txtPresetsEmpty.Visibility = _presets.Count == 0
                ? Visibility.Visible : Visibility.Collapsed;
        }

        // ── Сохранить выбор ───────────────────────────────────────────────────────

        private async void BtnSavePreset_Click(object sender, RoutedEventArgs e)
        {
            var selected = GetSelectedApps();
            if (selected.Count == 0) return;

            var dlg = new Views.PresetSaveDialog(selected.Count) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() != true) return;

            int? userId = UserSession.IsLoggedIn ? UserSession.UserId : (int?)null;
            var preset  = new Preset { Name = dlg.PresetName, Description = dlg.PresetDescription, Apps = selected };

            btnSavePreset.IsEnabled = false;
            try
            {
                var saved = await PresetService.SaveAsync(userId, preset);
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

        // ── Поделиться ────────────────────────────────────────────────────────────

        private async void BtnSharePreset_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is not Preset preset) return;

            if (!UserSession.IsLoggedIn || preset.IsLocal)
            {
                MessageBox.Show("Шаринг пресетов доступен только для облачных пресетов авторизованных пользователей.",
                    "Пресеты", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (preset.IsLoading) return;
            preset.IsLoading = true;

            string? code = await PresetService.ShareAsync(preset.Id);
            preset.IsLoading = false;

            if (code == null) { AppLogger.Write("❌ Не удалось получить ссылку на пресет"); return; }

            preset.ShareCode = code;
            string link = $"ven4tools.ru/p/{code}";
            try { Clipboard.SetText(link); } catch { }
            AppLogger.Write($"🔗 Ссылка скопирована: {link}");
        }

        // ── Удалить пресет ────────────────────────────────────────────────────────

        private async void BtnDeletePreset_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is not Preset preset) return;

            var r = MessageBox.Show($"Удалить пресет «{preset.Name}»?",
                "Пресеты", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;

            int? userId = UserSession.IsLoggedIn ? UserSession.UserId : (int?)null;
            bool deleted = await PresetService.DeleteAsync(userId, preset);
            if (!deleted)
            {
                AppLogger.Write($"❌ Не удалось удалить пресет «{preset.Name}» — сервер недоступен");
                MessageBox.Show("Не удалось удалить пресет. Сервер недоступен — попробуйте позже.",
                    "Пресеты", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _presets.Remove(preset);
            txtPresetsEmpty.Visibility = _presets.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            AppLogger.Write($"🗑️ Пресет «{preset.Name}» удалён");
        }

        // ── Импорт по коду ────────────────────────────────────────────────────────

        private async void BtnImportByCode_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Views.PresetCodeDialog { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.Code)) return;

            var preset = await PresetService.GetByCodeAsync(dlg.Code);
            if (preset == null)
            {
                MessageBox.Show("Пресет не найден или недоступен.", "Пресеты",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            AppLogger.Write($"📥 Загружен пресет «{preset.Name}» ({preset.Apps.Count} прил.)");
            ApplyPreset(preset);

            // Предложить сохранить
            var save = MessageBox.Show($"Пресет «{preset.Name}» применён.\n\nСохранить в свой список?",
                "Пресеты", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (save == MessageBoxResult.Yes)
            {
                int? userId = UserSession.IsLoggedIn ? UserSession.UserId : (int?)null;
                // Сохраняем как новый собственный пресет: серверный Id и код шаринга
                // чужого пресета сбрасываем, иначе перезапишем оригинал на сервере.
                preset.Id = 0;
                preset.ShareCode = null;
                var saved = await PresetService.SaveAsync(userId, preset);
                if (saved != null)
                {
                    _presets.Insert(0, saved);
                    txtPresetsEmpty.Visibility = Visibility.Collapsed;
                    AppLogger.Write($"✅ Пресет «{preset.Name}» сохранён");
                }
            }
        }
    }
}
