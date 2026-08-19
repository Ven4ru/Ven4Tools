using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Ven4Tools.Models;
using Ven4Tools.Services;

namespace Ven4Tools.ViewModels
{
    // Пресеты (сохранение/применение/переименование/обновление состава/удаление)
    // и экспорт/импорт списка выбранных приложений. Часть CatalogViewModel.
    public sealed partial class CatalogViewModel
    {
        // ── Пресеты ──────────────────────────────────────────────────────────────

        private bool _presetsEmpty = true;
        public bool PresetsEmpty { get => _presetsEmpty; set => SetField(ref _presetsEmpty, value); }

        private string _savePresetLabel = "💾 Сохранить выбор";
        public string SavePresetLabel { get => _savePresetLabel; set => SetField(ref _savePresetLabel, value); }

        private async Task RefreshPresetsAsync()
        {
            _pendingUpdatePreset = null;
            SavePresetLabel = "💾 Сохранить выбор";
            var list = await PresetService.LoadAsync();
            Presets.Clear();
            foreach (var p in list) Presets.Add(p);
            PresetsEmpty = Presets.Count == 0;
        }

        private async Task SavePresetAsync()
        {
            if (_pendingUpdatePreset != null)
            {
                var updating = _pendingUpdatePreset;
                _pendingUpdatePreset = null;
                SavePresetLabel = "💾 Сохранить выбор";

                var selectedIds = Apps.Where(a => a.IsSelected).Select(a => a.AppId).ToList();
                if (selectedIds.Count == 0) return;
                var previous = updating.Apps;
                updating.Apps = selectedIds;
                bool ok = await PresetService.UpdateAsync(updating);
                if (ok) updating.RaiseAppCountChanged(); else updating.Apps = previous;
                Log(ok ? $"✅ Состав пресета «{updating.Name}» обновлён ({selectedIds.Count} прил.)"
                       : $"❌ Не удалось обновить состав пресета «{updating.Name}»");
                return;
            }

            var selected = Apps.Where(a => a.IsSelected).Select(a => a.AppId).ToList();
            if (selected.Count == 0) return;

            var owner = OwnerWindowProvider?.Invoke();
            var dlg = new Views.PresetSaveDialog(selected.Count) { Owner = owner };
            if (dlg.ShowDialog() != true) return;

            var preset = new Preset { Name = dlg.PresetName, Description = dlg.PresetDescription, Apps = selected };
            var saved = await PresetService.SaveAsync(preset);
            if (saved == null) { Log("❌ Не удалось сохранить пресет"); return; }
            Presets.Insert(0, saved);
            PresetsEmpty = false;
            Log($"✅ Пресет «{saved.Name}» сохранён ({selected.Count} прил.)");
        }

        private void ApplyPreset(Preset preset)
        {
            int applied = 0;
            foreach (var id in preset.Apps)
            {
                var row = Apps.FirstOrDefault(a => a.AppId == id);
                if (row != null && row.IsSelectable)
                {
                    row.IsSelected = true;
                    applied++;
                }
            }
            Log($"📋 Пресет «{preset.Name}» применён: {applied} из {preset.Apps.Count} приложений отмечено");
        }

        private async Task RenamePresetAsync(Preset preset)
        {
            var owner = OwnerWindowProvider?.Invoke();
            var dlg = new Views.PresetSaveDialog(preset.Name, preset.Description) { Owner = owner };
            if (dlg.ShowDialog() != true) return;

            string oldName = preset.Name, oldDesc = preset.Description;
            preset.Name = dlg.PresetName;
            preset.Description = dlg.PresetDescription;
            bool ok = await PresetService.UpdateAsync(preset);
            if (ok) preset.RaiseNameChanged();
            else { preset.Name = oldName; preset.Description = oldDesc; }
            Log(ok ? $"✅ Пресет переименован: «{preset.Name}»" : $"❌ Не удалось переименовать пресет «{oldName}»");
        }

        private void BeginUpdatePresetComposition(Preset preset)
        {
            ApplyPreset(preset);
            _pendingUpdatePreset = preset;
            SavePresetLabel = $"↻ Обновить «{preset.Name}»";
        }

        private async Task DeletePresetAsync(Preset preset)
        {
            if (MessageBox.Show($"Удалить пресет «{preset.Name}»?", "Пресеты",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            if (_pendingUpdatePreset == preset)
            {
                _pendingUpdatePreset = null;
                SavePresetLabel = "💾 Сохранить выбор";
            }
            await PresetService.DeleteAsync(preset);
            Presets.Remove(preset);
            PresetsEmpty = Presets.Count == 0;
            Log($"🗑️ Пресет «{preset.Name}» удалён");
        }
        // ── Экспорт/импорт списка ────────────────────────────────────────────────

        private void ExportList()
        {
            var selected = Apps.Where(a => a.IsSelected).Select(a => a.AppId).ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show("Нет выбранных приложений для экспорта.", "Экспорт", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Экспорт списка приложений",
                Filter = "JSON файлы (*.json)|*.json",
                FileName = $"ven4tools_list_{DateTime.Now:yyyyMMdd_HHmm}.json",
                DefaultExt = ".json"
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var payload = new { exported_at = DateTime.Now.ToString("o"), app_ids = selected.OrderBy(id => id).ToList() };
                string json = Newtonsoft.Json.JsonConvert.SerializeObject(payload, Newtonsoft.Json.Formatting.Indented);
                File.WriteAllText(dlg.FileName, json, System.Text.Encoding.UTF8);
                Log($"📤 Экспорт: {selected.Count} приложений → {Path.GetFileName(dlg.FileName)}");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка при сохранении:\n{ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Импорт набора, собранного на сайте: человек вводит короткий код,
        /// клиент забирает состав с ven4tools.ru и отмечает те же приложения.
        /// Ничего не устанавливает сам — выбор остаётся за пользователем.
        /// </summary>
        private async Task ImportPresetByCodeAsync()
        {
            var dlg = new Views.PresetCodeDialog { Owner = Application.Current?.MainWindow };
            if (dlg.ShowDialog() != true) return;

            var result = await SitePresetService.FetchAsync(dlg.Code);
            if (!result.Success)
            {
                Log($"📥 Набор с сайта: {result.Error}");
                MessageBox.Show(result.Error, "Набор не загружен", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            int matched = 0;
            var missing = new List<string>();
            foreach (var id in result.AppIds)
            {
                var row = Apps.FirstOrDefault(a => a.AppId == id);
                if (row != null) { row.IsSelected = true; matched++; }
                else missing.Add(id);
            }

            Log($"📥 Набор V4T-{result.Code}: отмечено {matched}, не найдено в каталоге: {missing.Count}");

            if (missing.Count > 0)
            {
                // Набор мог быть собран при более широкой области каталога, чем
                // выбрана здесь, — это не ошибка, но человек должен знать, что
                // отмечено не всё, иначе он решит, что установил весь набор.
                MessageBox.Show(
                    $"Отмечено приложений: {matched}{Environment.NewLine}" +
                    $"Не найдено в текущем каталоге: {missing.Count}{Environment.NewLine}{Environment.NewLine}" +
                    string.Join(", ", missing) + Environment.NewLine + Environment.NewLine +
                    "Возможно, набор собран при более широкой области каталога — проверьте её в настройках.",
                    "Набор загружен частично", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void ImportList()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Импорт списка приложений", Filter = "JSON файлы (*.json)|*.json" };
            if (dlg.ShowDialog() != true) return;
            try
            {
                string json = File.ReadAllText(dlg.FileName, System.Text.Encoding.UTF8);
                var doc = Newtonsoft.Json.Linq.JObject.Parse(json);
                var ids = doc["app_ids"]?.ToObject<List<string>>() ?? doc["apps"]?.ToObject<List<string>>() ?? new List<string>();

                int matched = 0, skipped = 0;
                foreach (var id in ids)
                {
                    var row = Apps.FirstOrDefault(a => a.AppId == id);
                    if (row != null) { row.IsSelected = true; matched++; } else skipped++;
                }
                Log($"📥 Импорт: отмечено {matched}, не найдено в каталоге: {skipped}");
                if (skipped > 0)
                    MessageBox.Show($"Отмечено: {matched}\nНе найдено в текущем каталоге: {skipped}", "Импорт завершён",
                        MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка чтения файла:\n{ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
