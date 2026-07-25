using System;
using System.Collections.Generic;
using System.IO;
using System.Management;
using System.Threading.Tasks;
using Ven4Tools.Models;

namespace Ven4Tools.Services.DiskBenchmark
{
    /// <summary>
    /// Перечисляет физические накопители системы, их шину, тип носителя и тома.
    ///
    /// Серийный номер сознательно не читается и не показывается: отчёт пользователь копирует
    /// и может опубликовать, а серийный номер — идентификатор конкретного устройства.
    /// </summary>
    public static class DiskInventoryService
    {
        public static Task<List<PhysicalDiskInfo>> GetDisksAsync() => Task.Run(() =>
        {
            var disks = new List<PhysicalDiskInfo>();

            // Сведения о шине и носителе живут в пространстве имён хранилища.
            var storage = ReadStorageDisks();

            try
            {
                // Выборка целиком, а не проекцией: связанные объекты берутся через GetRelated,
                // а тому нужен полный путь исходного объекта.
                using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_DiskDrive");

                foreach (ManagementObject drive in searcher.Get())
                {
                    using (drive)
                    {
                        try
                        {
                            uint index = Convert.ToUInt32(drive["Index"]);
                            string pnpId = drive["PNPDeviceID"]?.ToString() ?? "";

                            storage.TryGetValue(index, out var extra);

                            var disk = new PhysicalDiskInfo
                            {
                                Index = index,
                                FriendlyName = !string.IsNullOrWhiteSpace(extra?.Name)
                                    ? extra!.Name
                                    : (drive["Model"]?.ToString() ?? "Накопитель " + index),
                                SizeBytes = extra?.Size > 0 ? extra.Size : ToInt64(drive["Size"]),
                                Bus = extra?.Bus ?? DiskBusKind.Unknown,
                                Media = extra?.Media ?? DiskMediaKind.Unknown,
                                SpindleSpeed = extra?.SpindleSpeed ?? 0
                            };

                            foreach (var volume in ReadVolumes(drive))
                                disk.Volumes.Add(volume);

                            // Только для NVMe: у SATA и USB параметры интерфейса через этот путь
                            // недостижимы, и гадать по косвенным признакам мы не будем.
                            if (disk.Bus == DiskBusKind.Nvme)
                                disk.Link = PciLinkResolver.Resolve(pnpId);

                            disks.Add(disk);
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Write(ex, "DiskInventoryService.GetDisksAsync.Drive");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "DiskInventoryService.GetDisksAsync");
            }

            disks.Sort((left, right) => left.Index.CompareTo(right.Index));
            return disks;
        });

        private sealed class StorageDiskData
        {
            public string Name = "";
            public long Size;
            public DiskBusKind Bus;
            public DiskMediaKind Media;
            public uint SpindleSpeed;
        }

        /// <summary>
        /// Читает MSFT_PhysicalDisk. Если пространство имён недоступно, возвращается пустой
        /// словарь — тогда шина и тип носителя останутся неизвестными, и это будет честно
        /// показано пользователю.
        /// </summary>
        private static Dictionary<uint, StorageDiskData> ReadStorageDisks()
        {
            var result = new Dictionary<uint, StorageDiskData>();
            try
            {
                var scope = new ManagementScope(@"root\Microsoft\Windows\Storage");
                using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(
                    "SELECT DeviceId, FriendlyName, Size, BusType, MediaType, SpindleSpeed FROM MSFT_PhysicalDisk"));

                foreach (ManagementObject disk in searcher.Get())
                {
                    using (disk)
                    {
                        if (!uint.TryParse(disk["DeviceId"]?.ToString(), out uint index)) continue;

                        uint spindle = 0;
                        if (disk["SpindleSpeed"] != null)
                        {
                            uint raw = Convert.ToUInt32(disk["SpindleSpeed"]);
                            // 0 и 0xFFFFFFFF означают «не применимо или не определено».
                            if (raw > 0 && raw != uint.MaxValue) spindle = raw;
                        }

                        result[index] = new StorageDiskData
                        {
                            Name = disk["FriendlyName"]?.ToString() ?? "",
                            Size = ToInt64(disk["Size"]),
                            Bus = MapBus(disk["BusType"]),
                            Media = MapMedia(disk["MediaType"]),
                            SpindleSpeed = spindle
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "DiskInventoryService.ReadStorageDisks");
            }
            return result;
        }

        /// <summary>
        /// Тома накопителя: Win32_DiskDrive → разделы → логические диски.
        /// Связанные объекты берутся через GetRelated: запрос ASSOCIATORS OF, собранный
        /// вручную, не находит накопитель из-за обратных слэшей в ключе.
        /// </summary>
        private static List<BenchmarkVolumeInfo> ReadVolumes(ManagementObject drive)
        {
            var volumes = new List<BenchmarkVolumeInfo>();

            string? systemRoot = null;
            try
            {
                systemRoot = Path.GetPathRoot(Environment.SystemDirectory);
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "DiskInventoryService.ReadVolumes.SystemRoot");
            }

            try
            {
                foreach (ManagementObject partition in drive.GetRelated("Win32_DiskPartition"))
                {
                    using (partition)
                    {
                        foreach (ManagementObject logicalDisk in partition.GetRelated("Win32_LogicalDisk"))
                        {
                            using (logicalDisk)
                            {
                                string letter = logicalDisk["DeviceID"]?.ToString() ?? "";
                                if (string.IsNullOrWhiteSpace(letter)) continue;
                                volumes.Add(BuildVolume(letter, systemRoot));
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "DiskInventoryService.ReadVolumes");
            }

            return volumes;
        }

        private static BenchmarkVolumeInfo BuildVolume(string letter, string? systemRoot)
        {
            bool ready = false;
            bool removable = false;
            long total = 0;
            long free = 0;
            string label = "";

            try
            {
                var drive = new DriveInfo(letter);
                ready = drive.IsReady;
                removable = drive.DriveType == DriveType.Removable;
                if (ready)
                {
                    total = drive.TotalSize;
                    free = drive.AvailableFreeSpace;
                    label = drive.VolumeLabel ?? "";
                }
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "DiskInventoryService.BuildVolume");
            }

            bool isSystem = systemRoot != null &&
                            systemRoot.TrimEnd('\\').Equals(letter.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

            return new BenchmarkVolumeInfo
            {
                Letter = letter,
                Label = label,
                TotalBytes = total,
                FreeBytes = free,
                IsReady = ready,
                IsSystem = isSystem,
                IsRemovable = removable
            };
        }

        private static long ToInt64(object? value)
        {
            if (value == null) return 0;
            try
            {
                return Convert.ToInt64(value);
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "DiskInventoryService.ToInt64");
                return 0;
            }
        }

        private static DiskBusKind MapBus(object? value)
        {
            if (value == null) return DiskBusKind.Unknown;
            ushort code;
            try
            {
                code = Convert.ToUInt16(value);
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "DiskInventoryService.MapBus");
                return DiskBusKind.Unknown;
            }

            return code switch
            {
                1 => DiskBusKind.Scsi,
                2 => DiskBusKind.Atapi,
                3 => DiskBusKind.Ata,
                4 => DiskBusKind.Ieee1394,
                5 => DiskBusKind.Ssa,
                6 => DiskBusKind.FibreChannel,
                7 => DiskBusKind.Usb,
                8 => DiskBusKind.Raid,
                9 => DiskBusKind.IScsi,
                10 => DiskBusKind.Sas,
                11 => DiskBusKind.Sata,
                12 => DiskBusKind.Sd,
                13 => DiskBusKind.Mmc,
                14 => DiskBusKind.Virtual,
                15 => DiskBusKind.FileBackedVirtual,
                16 => DiskBusKind.StorageSpaces,
                17 => DiskBusKind.Nvme,
                18 => DiskBusKind.Scm,
                19 => DiskBusKind.Ufs,
                _ => DiskBusKind.Unknown
            };
        }

        private static DiskMediaKind MapMedia(object? value)
        {
            if (value == null) return DiskMediaKind.Unknown;
            try
            {
                return Convert.ToUInt16(value) switch
                {
                    3 => DiskMediaKind.Hdd,
                    4 => DiskMediaKind.Ssd,
                    5 => DiskMediaKind.Scm,
                    _ => DiskMediaKind.Unknown
                };
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "DiskInventoryService.MapMedia");
                return DiskMediaKind.Unknown;
            }
        }

        /// <summary>Человекочитаемое название шины.</summary>
        public static string DescribeBus(DiskBusKind bus) => bus switch
        {
            DiskBusKind.Nvme => "NVMe",
            DiskBusKind.Sata => "SATA",
            DiskBusKind.Ata => "ATA",
            DiskBusKind.Atapi => "ATAPI",
            DiskBusKind.Usb => "USB",
            DiskBusKind.Sas => "SAS",
            DiskBusKind.Scsi => "SCSI",
            DiskBusKind.Raid => "RAID",
            DiskBusKind.IScsi => "iSCSI",
            DiskBusKind.Sd => "SD",
            DiskBusKind.Mmc => "MMC",
            DiskBusKind.Ieee1394 => "IEEE 1394",
            DiskBusKind.Ssa => "SSA",
            DiskBusKind.FibreChannel => "Fibre Channel",
            DiskBusKind.Virtual => "виртуальный диск",
            DiskBusKind.FileBackedVirtual => "виртуальный диск в файле",
            DiskBusKind.StorageSpaces => "дисковые пространства",
            DiskBusKind.Scm => "память класса хранения",
            DiskBusKind.Ufs => "UFS",
            _ => "неизвестно"
        };

        /// <summary>Человекочитаемый тип носителя.</summary>
        public static string DescribeMedia(DiskMediaKind media) => media switch
        {
            DiskMediaKind.Ssd => "SSD",
            DiskMediaKind.Hdd => "жёсткий диск",
            DiskMediaKind.Scm => "память класса хранения",
            _ => "неизвестно"
        };
    }
}
