using System;
using System.Management;
using Ven4Tools.Models;

namespace Ven4Tools.Services.DiskBenchmark
{
    /// <summary>
    /// Пытается определить поколение и число линий PCIe для накопителя.
    ///
    /// Данные берутся из свойств узла устройства: от диска поднимаемся по родителям до узла
    /// на шине PCI и читаем у него согласованные контроллером параметры линии. Путь хрупкий —
    /// на части систем свойства не заполнены, на виртуальных машинах узла PCI может не быть
    /// вовсе. Любая неудача даёт «неизвестно»: правдоподобное значение по модели накопителя
    /// или по названию контроллера не подставляется никогда.
    ///
    /// Вынесено из инвентаризации отдельно именно поэтому — отказ здесь не должен мешать
    /// показать список накопителей.
    /// </summary>
    public static class PciLinkResolver
    {
        private const string DevPkeyDeviceParent = "{4340A6C5-93FA-4706-972C-7B648008A5A7} 8";
        private const string DevPkeyPciCurrentLinkSpeed = "{3AB22E31-8264-4B4E-9AF5-A8D2D8E33E62} 9";
        private const string DevPkeyPciCurrentLinkWidth = "{3AB22E31-8264-4B4E-9AF5-A8D2D8E33E62} 10";

        /// <summary>Сколько уровней вверх готовы пройти в поисках узла на шине PCI.</summary>
        private const int MaxParentHops = 4;

        public static PciLinkInfo Resolve(string? diskPnpDeviceId)
        {
            if (string.IsNullOrWhiteSpace(diskPnpDeviceId)) return PciLinkInfo.Unknown;

            try
            {
                string? current = diskPnpDeviceId;

                for (int hop = 0; hop < MaxParentHops && !string.IsNullOrWhiteSpace(current); hop++)
                {
                    if (current!.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase))
                    {
                        int speedCode = ReadUInt32Property(current, DevPkeyPciCurrentLinkSpeed);
                        int width = ReadUInt32Property(current, DevPkeyPciCurrentLinkWidth);

                        // Оба значения обязаны быть получены — иначе честное «неизвестно».
                        if (speedCode >= 1 && speedCode <= 5 && width > 0)
                            return new PciLinkInfo { Generation = speedCode, Width = width };

                        return PciLinkInfo.Unknown;
                    }

                    current = ReadStringProperty(current, DevPkeyDeviceParent);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "PciLinkResolver.Resolve");
            }

            return PciLinkInfo.Unknown;
        }

        /// <summary>
        /// Сущность ищется запросом, а не собирается путём вручную: путь с ключом,
        /// содержащим обратные слэши и амперсанды, WMI не находит.
        /// </summary>
        private static ManagementBaseObject[]? QueryProperties(string deviceId, string propertyKey)
        {
            try
            {
                string escaped = deviceId.Replace("\\", "\\\\").Replace("'", "\\'");
                using var searcher = new ManagementObjectSearcher(
                    $"SELECT * FROM Win32_PnPEntity WHERE DeviceID = '{escaped}'");

                foreach (ManagementObject entity in searcher.Get())
                {
                    using (entity)
                    {
                        using var input = entity.GetMethodParameters("GetDeviceProperties");
                        input["devicePropertyKeys"] = new[] { propertyKey };

                        using var output = entity.InvokeMethod("GetDeviceProperties", input, null);
                        return output?["deviceProperties"] as ManagementBaseObject[];
                    }
                }
                return null;
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "PciLinkResolver.QueryProperties");
                return null;
            }
        }

        private static string? ReadStringProperty(string deviceId, string propertyKey)
        {
            var properties = QueryProperties(deviceId, propertyKey);
            if (properties == null) return null;

            foreach (var property in properties)
            {
                using (property)
                {
                    var data = property["Data"];
                    if (data != null) return data.ToString();
                }
            }
            return null;
        }

        /// <summary>Читает целочисленное свойство. Ноль означает «не получено».</summary>
        private static int ReadUInt32Property(string deviceId, string propertyKey)
        {
            var properties = QueryProperties(deviceId, propertyKey);
            if (properties == null) return 0;

            foreach (var property in properties)
            {
                using (property)
                {
                    var data = property["Data"];
                    if (data == null) continue;
                    try
                    {
                        return Convert.ToInt32(data);
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Write(ex, "PciLinkResolver.ReadUInt32Property");
                    }
                }
            }
            return 0;
        }
    }
}
