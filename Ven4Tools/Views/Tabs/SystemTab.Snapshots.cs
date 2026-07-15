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
        // ── Снапшоты конфигурации ────────────────────────────────────────────────

        private readonly System.Collections.ObjectModel.ObservableCollection<ConfigSnapshotInfo> _snapshots = new();

        private void LoadSnapshotsList()
        {
            _snapshots.Clear();
            foreach (var s in ConfigSnapshotService.GetSnapshots())
                _snapshots.Add(s);

            lstSnapshots.ItemsSource = _snapshots;
            txtSnapshotsEmpty.Visibility = _snapshots.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private DebloaterTab? GetDebloaterTab() =>
            Window.GetWindow(this) is MainWindow mw ? mw.EnsureDebloaterTab() : null;

        private async void BtnSaveSnapshot_Click(object sender, RoutedEventArgs e)
        {
            var debloaterTab = GetDebloaterTab();
            var tweakIds = debloaterTab?.GetSelectedTweakIds() ?? new List<string>();
            var presets = await PresetService.LoadAsync();

            var dlg = new Views.SnapshotNameDialog(tweakIds.Count, presets.Count)
                { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() != true) return;

            btnSaveSnapshot.IsEnabled = false;
            try
            {
                string? path = await ConfigSnapshotService.SaveAsync(dlg.SnapshotName, tweakIds);
                txtSnapshotStatus.Text = path != null
                    ? $"✅ Снапшот «{dlg.SnapshotName}» сохранён {DateTime.Now:HH:mm:ss}"
                    : "❌ Не удалось сохранить снапшот";
                LoadSnapshotsList();
            }
            finally { btnSaveSnapshot.IsEnabled = true; }
        }

        private async void BtnRestoreSnapshot_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is not ConfigSnapshotInfo info) return;

            var snapshot = ConfigSnapshotService.Load(info.FilePath);
            if (snapshot == null)
            {
                MessageBox.Show("Не удалось прочитать файл снапшота — он повреждён или несовместим.",
                    "Снапшоты", MessageBoxButton.OK, MessageBoxImage.Error);
                LoadSnapshotsList();
                return;
            }

            var confirm = MessageBox.Show(
                $"Восстановить состояние из снапшота «{snapshot.Name}»?\n\n" +
                $"Будет применено твиков: {snapshot.DebloatTweakIds.Count}\n" +
                $"Локальные пресеты будут заменены содержимым снапшота ({snapshot.Presets.Count} шт.)\n\n" +
                "Применение твиков Debloater (реестр/службы/удаление приложений) выполняется тем же способом, что и на вкладке «Очистка».",
                "Снапшоты — подтверждение восстановления",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            var btn = (Button)sender;
            btn.IsEnabled = false;
            try
            {
                var debloaterTab = GetDebloaterTab();
                int succeeded = 0, total = 0;
                if (debloaterTab != null && snapshot.DebloatTweakIds.Count > 0)
                {
                    // Восстановление твиков делает те же необратимые системные изменения
                    // (реестр/службы/удаление Appx), что и кнопка «Применить» на вкладке
                    // «Очистка» — там перед этим создаётся точка восстановления, здесь она
                    // нужна ровно по той же причине.
                    var rpOutcome = await UiGuards.ConfirmAndCreateRestorePointAsync(
                        $"Будет применено твиков: {snapshot.DebloatTweakIds.Count}.\n\nСоздать точку восстановления Windows перед восстановлением снапшота?",
                        "Ven4Tools — перед восстановлением снапшота");
                    if (rpOutcome == RestorePointOutcome.Cancelled)
                    {
                        txtSnapshotStatus.Text = "Отменено";
                        return;
                    }

                    var progress = new Progress<string>(name => txtSnapshotStatus.Text = $"⚙️ {name}...");
                    (succeeded, total) = await debloaterTab.ApplyTweaksByIdsAsync(snapshot.DebloatTweakIds, progress);
                    debloaterTab.SetSelectedTweakIds(snapshot.DebloatTweakIds);
                }
                else
                {
                    txtSnapshotStatus.Text = "⏳ Восстанавливаю снапшот...";
                }

                bool presetsOk = await ConfigSnapshotService.RestorePresetsAsync(snapshot);

                txtSnapshotStatus.Text =
                    $"✅ Восстановлено {DateTime.Now:HH:mm:ss}: твиков {succeeded}/{total}" +
                    (presetsOk ? $", пресетов {snapshot.Presets.Count}" : ", ошибка восстановления пресетов");
                AppLogger.Write($"📸 Снапшот «{snapshot.Name}» восстановлен: твиков {succeeded}/{total}, пресетов {snapshot.Presets.Count}");
            }
            catch (Exception ex)
            {
                txtSnapshotStatus.Text = $"❌ Ошибка восстановления: {ex.Message}";
                AppLogger.Write($"[Снапшоты] Ошибка восстановления: {ex.Message}");
            }
            finally { btn.IsEnabled = true; }
        }

        private void BtnDeleteSnapshot_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is not ConfigSnapshotInfo info) return;

            var r = MessageBox.Show($"Удалить снапшот «{info.Name}»?",
                "Снапшоты", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;

            if (ConfigSnapshotService.Delete(info.FilePath))
            {
                _snapshots.Remove(info);
                txtSnapshotsEmpty.Visibility = _snapshots.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                AppLogger.Write($"🗑️ Снапшот «{info.Name}» удалён");
            }
        }
    }
}
