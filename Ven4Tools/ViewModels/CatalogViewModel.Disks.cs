using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ven4Tools.Services;

namespace Ven4Tools.ViewModels
{
    // Выбор диска установки и подсчёт требуемого/свободного места.
    // Часть CatalogViewModel.
    public sealed partial class CatalogViewModel
    {
        // ── Диск установки ──────────────────────────────────────────────────────

        private string _spaceStatus = "";
        public string SpaceStatus { get => _spaceStatus; set => SetField(ref _spaceStatus, value); }

        private DiskOption? _selectedDisk;
        public DiskOption? SelectedDisk
        {
            get => _selectedDisk;
            set
            {
                if (SetField(ref _selectedDisk, value) && value != null)
                {
                    SelectedInstallDrive = value.Name + "\\";
                    UpdateDiskSpaceInfo();
                    _ = UpdateSpaceStatusAsync();
                }
            }
        }

        private void LoadAvailableDisks()
        {
            try
            {
                string systemDrive = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') ?? "C:";
                var drives = DriveInfo.GetDrives()
                    .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                    .Select(d => new DiskOption(d.RootDirectory.FullName.TrimEnd('\\'),
                        $"{d.Name.TrimEnd('\\')} ({d.AvailableFreeSpace / 1024 / 1024 / 1024:F1} ГБ свободно)"))
                    .ToList();

                AvailableDisks.Clear();
                foreach (var d in drives) AvailableDisks.Add(d);

                var systemDisk = drives.FirstOrDefault(d => d.Name == systemDrive);
                SelectedDisk = systemDisk ?? drives.FirstOrDefault();
                UpdateDiskSpaceInfo();
            }
            catch (Exception ex) { Log($"⚠️ Ошибка получения списка дисков: {ex.Message}"); }
        }

        private void UpdateDiskSpaceInfo()
        {
            try
            {
                string disk = SelectedInstallDrive.TrimEnd('\\');
                var drive = new DriveInfo(disk);
                if (drive.IsReady)
                    SpaceStatus = $"💾 Диск {disk} | Свободно: {drive.AvailableFreeSpace / 1024 / 1024 / 1024} ГБ / {drive.TotalSize / 1024 / 1024 / 1024} ГБ";
            }
            catch (Exception ex) { Log($"⚠️ Ошибка обновления информации о диске: {ex.Message}"); }
        }

        private async Task UpdateSpaceStatusAsync()
        {
            try
            {
                var selected = Apps.Where(a => a.IsSelected).ToList();
                using var sem = new SemaphoreSlim(5);
                long totalRequired = 0;
                var lockObj = new object();

                await Task.WhenAll(selected.Select(async row =>
                {
                    await sem.WaitAsync();
                    try
                    {
                        var result = await _availabilityChecker.CheckAppAvailabilityWithSize(row.App);
                        long mb = result.Status == AvailabilityChecker.AvailabilityStatus.Available ? result.SizeMB : 100;
                        lock (lockObj) { totalRequired += mb; }
                    }
                    finally { sem.Release(); }
                }));

                string disk = SelectedInstallDrive.TrimEnd('\\');
                var drive = new DriveInfo(disk);
                if (drive.IsReady)
                {
                    long availableMB = drive.AvailableFreeSpace / 1024 / 1024;
                    SpaceStatus = availableMB >= totalRequired
                        ? $"💾 Диск {disk} | Требуется: ~{totalRequired} МБ | Доступно: {availableMB} МБ ✅"
                        : $"💾 Диск {disk} | Требуется: ~{totalRequired} МБ | Доступно: {availableMB} МБ ❌ Мало места!";
                }
            }
            catch (Exception ex) { Log($"⚠️ Ошибка проверки места: {ex.Message}"); }
        }
    }
}
