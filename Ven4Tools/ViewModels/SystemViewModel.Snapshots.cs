using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using Ven4Tools.Models;
using Ven4Tools.Services;
using Ven4Tools.Views;

namespace Ven4Tools.ViewModels
{
    public sealed partial class SystemViewModel
    {
        public ObservableCollection<SnapshotRow> Snapshots { get; } = new();

        private bool _showSnapshotsEmpty = true;
        public bool ShowSnapshotsEmpty { get => _showSnapshotsEmpty; private set => SetField(ref _showSnapshotsEmpty, value); }

        private string _snapshotStatusText = "";
        public string SnapshotStatusText { get => _snapshotStatusText; private set => SetField(ref _snapshotStatusText, value); }

        private bool _isSavingSnapshot;
        public bool IsSavingSnapshot
        {
            get => _isSavingSnapshot;
            private set { if (SetField(ref _isSavingSnapshot, value)) SaveSnapshotCommand.RaiseCanExecuteChanged(); }
        }

        private void LoadSnapshotsList() => ApplySnapshots(ConfigSnapshotService.GetSnapshots());

        // Отделено от чтения диска, чтобы InitializeAsync могла выполнить обход папки
        // снапшотов в пуле потоков и наполнить коллекцию уже в потоке UI.
        private void ApplySnapshots(List<ConfigSnapshotInfo> infos)
        {
            Snapshots.Clear();
            foreach (var s in infos)
                Snapshots.Add(new SnapshotRow(s));

            ShowSnapshotsEmpty = Snapshots.Count == 0;
        }

        private async Task RunSaveSnapshotAsync()
        {
            if (IsSavingSnapshot) return;

            var debloaterTab = DebloaterTabProvider?.Invoke();
            var tweakIds = debloaterTab?.GetSelectedTweakIds() ?? new List<string>();
            var presets = await PresetService.LoadAsync();

            var dlg = new SnapshotNameDialog(tweakIds.Count, presets.Count) { Owner = OwnerWindowProvider?.Invoke() };
            if (dlg.ShowDialog() != true) return;

            IsSavingSnapshot = true;
            try
            {
                string? path = await ConfigSnapshotService.SaveAsync(dlg.SnapshotName, tweakIds);
                SnapshotStatusText = path != null
                    ? $"✅ Снапшот «{dlg.SnapshotName}» сохранён {DateTime.Now:HH:mm:ss}"
                    : "❌ Не удалось сохранить снапшот";
                LoadSnapshotsList();
            }
            finally { IsSavingSnapshot = false; }
        }

        private async Task RunRestoreSnapshotAsync(SnapshotRow? row)
        {
            if (row == null || row.IsRestoring) return;

            var snapshot = ConfigSnapshotService.Load(row.Info.FilePath);
            if (snapshot == null)
            {
                MessageBox.Show("Не удалось прочитать файл снапшота — он повреждён или несовместим.",
                    "Снапшоты", MessageBoxButton.OK, MessageBoxImage.Error);
                LoadSnapshotsList();
                return;
            }

            row.IsRestoring = true;
            try
            {
                var debloaterTab = DebloaterTabProvider?.Invoke();
                int succeeded = 0, total = 0;
                if (debloaterTab != null && snapshot.DebloatTweakIds.Count > 0)
                {
                    // Отказ по занятости — до подтверждения и до создания точки
                    // восстановления: иначе пользователь проходит диалог, ждёт точку
                    // восстановления и только потом узнаёт, что применить твики нельзя.
                    // Гейт внутри ApplyTweaksByIdsAsync остаётся как защита от гонки
                    // («Применить» могли нажать, пока открыт диалог) — здесь он лишь
                    // отрабатывает раньше в подавляющем большинстве случаев.
                    if (debloaterTab.IsApplyBusy)
                    {
                        SnapshotStatusText = "⏳ Идёт очистка системы — дождитесь её завершения";
                        MessageBox.Show(
                            "Сейчас выполняется очистка системы на вкладке «Очистка». " +
                            "Восстановление снапшота применяет те же твики, поэтому его нельзя запустить одновременно.\n\n" +
                            "Дождитесь окончания очистки и повторите.",
                            "Снапшоты", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }

                    // Единый диалог: подтверждение восстановления (Отмена = прервать) +
                    // предложение точки восстановления. Восстановление твиков делает те же
                    // необратимые системные изменения (реестр/службы/удаление Appx), что и
                    // «Применить» на вкладке «Очистка», поэтому точка восстановления нужна
                    // здесь по той же причине.
                    var rpOutcome = await UiGuards.ConfirmAndCreateRestorePointAsync(
                        $"Восстановить состояние из снапшота «{snapshot.Name}»?\n\n" +
                        $"Будет применено твиков: {snapshot.DebloatTweakIds.Count} (реестр/службы/удаление приложений, как на вкладке «Очистка»).\n" +
                        $"Локальные пресеты будут заменены содержимым снапшота ({snapshot.Presets.Count} шт.).\n\n" +
                        "Создать точку восстановления Windows перед восстановлением снапшота?",
                        "Ven4Tools — перед восстановлением снапшота");
                    if (rpOutcome == RestorePointOutcome.Cancelled)
                    {
                        SnapshotStatusText = "Отменено";
                        return;
                    }

                    var progress = new Progress<string>(name => SnapshotStatusText = $"⚙️ {name}...");
                    (succeeded, total) = await debloaterTab.ApplyTweaksByIdsAsync(snapshot.DebloatTweakIds, progress);
                    debloaterTab.SetSelectedTweakIds(snapshot.DebloatTweakIds);
                }
                else
                {
                    // Твиков нет — меняются только локальные пресеты, точка восстановления
                    // не относится к делу. Одно подтверждение действия.
                    var confirm = MessageBox.Show(
                        $"Восстановить состояние из снапшота «{snapshot.Name}»?\n\n" +
                        $"Локальные пресеты будут заменены содержимым снапшота ({snapshot.Presets.Count} шт.).",
                        "Снапшоты — подтверждение восстановления",
                        MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (confirm != MessageBoxResult.Yes) return;

                    SnapshotStatusText = "⏳ Восстанавливаю снапшот...";
                }

                bool presetsOk = await ConfigSnapshotService.RestorePresetsAsync(snapshot);

                SnapshotStatusText =
                    $"✅ Восстановлено {DateTime.Now:HH:mm:ss}: твиков {succeeded}/{total}" +
                    (presetsOk ? $", пресетов {snapshot.Presets.Count}" : ", ошибка восстановления пресетов");
                AppLogger.Write($"📸 Снапшот «{snapshot.Name}» восстановлен: твиков {succeeded}/{total}, пресетов {snapshot.Presets.Count}");
            }
            catch (Exception ex)
            {
                SnapshotStatusText = $"❌ Ошибка восстановления: {ex.Message}";
                AppLogger.Write($"[Снапшоты] Ошибка восстановления: {ex.Message}");
            }
            finally { row.IsRestoring = false; }
        }

        private void DeleteSnapshot(SnapshotRow? row)
        {
            if (row == null) return;

            var r = MessageBox.Show($"Удалить снапшот «{row.Info.Name}»?",
                "Снапшоты", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;

            if (ConfigSnapshotService.Delete(row.Info.FilePath))
            {
                Snapshots.Remove(row);
                ShowSnapshotsEmpty = Snapshots.Count == 0;
                AppLogger.Write($"🗑️ Снапшот «{row.Info.Name}» удалён");
            }
        }
    }
}
