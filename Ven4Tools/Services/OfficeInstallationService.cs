using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Ven4Tools.Services
{
    public enum OfficeInstallationKind
    {
        NotFound,
        ClickToRun,
        Msi
    }

    /// <summary>
    /// Установленный на машине Office — от <see cref="OfficeInstallationService.Detect"/>.
    /// <see cref="ProductIds"/> заполнен только для C2R (нужен для адресного удаления
    /// через ODT); MSI не даёт ProductReleaseIds, поэтому для него список пуст.
    /// </summary>
    public sealed class InstalledOfficeInfo
    {
        public OfficeInstallationKind Kind { get; init; } = OfficeInstallationKind.NotFound;
        public string DisplayName { get; init; } = "";
        public string Platform { get; init; } = "";
        public string Culture { get; init; } = "";
        public string Version { get; init; } = "";
        public IReadOnlyList<string> ProductIds { get; init; } = Array.Empty<string>();

        public static readonly InstalledOfficeInfo None = new();
    }

    /// <summary>
    /// Seam над реестром — по образцу IClientIntegrityEnvironment (Ven4Tools.Launcher/
    /// Services/ClientIntegrityChecker.cs): реальный реестр непригоден для юнит-теста,
    /// разбор строк тестируется отдельно от чтения ключей.
    /// </summary>
    internal interface IOfficeRegistryReader
    {
        /// <summary>
        /// Значения HKLM\SOFTWARE\Microsoft\Office\ClickToRun\Configuration
        /// (ProductReleaseIds/Platform/ClientCulture/VersionToReport). Пустой словарь,
        /// если ключа нет или C2R не установлен.
        /// </summary>
        IReadOnlyDictionary<string, string> ReadClickToRunConfiguration();

        /// <summary>
        /// DisplayName/DisplayVersion из HKLM\...\Uninstall (оба представления,
        /// 64- и 32-битное) — без фильтрации, фильтрация "это Office?" происходит
        /// в OfficeInstallationService, чтобы оставаться тестируемой без реестра.
        /// </summary>
        IReadOnlyList<(string DisplayName, string DisplayVersion)> ReadUninstallDisplayEntries();
    }

    internal sealed class RealOfficeRegistryReader : IOfficeRegistryReader
    {
        private const string ClickToRunKey = @"SOFTWARE\Microsoft\Office\ClickToRun\Configuration";
        private static readonly string[] UninstallKeys =
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
        };

        public IReadOnlyDictionary<string, string> ReadClickToRunConfiguration()
        {
            var result = new Dictionary<string, string>();
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(ClickToRunKey);
                if (key == null) return result;

                foreach (string name in new[] { "ProductReleaseIds", "Platform", "ClientCulture", "VersionToReport" })
                {
                    string? value = key.GetValue(name)?.ToString();
                    if (!string.IsNullOrEmpty(value)) result[name] = value;
                }
            }
            catch (Exception ex) { AppLogger.Write($"[OfficeInstallationService] Чтение ClickToRun\\Configuration: {ex.Message}"); }
            return result;
        }

        public IReadOnlyList<(string DisplayName, string DisplayVersion)> ReadUninstallDisplayEntries()
        {
            var result = new List<(string, string)>();
            foreach (string uninstallKey in UninstallKeys)
            {
                try
                {
                    using var root = Registry.LocalMachine.OpenSubKey(uninstallKey);
                    if (root == null) continue;

                    foreach (string subKeyName in root.GetSubKeyNames())
                    {
                        try
                        {
                            using var subKey = root.OpenSubKey(subKeyName);
                            string? displayName = subKey?.GetValue("DisplayName")?.ToString();
                            if (string.IsNullOrEmpty(displayName)) continue;
                            string displayVersion = subKey?.GetValue("DisplayVersion")?.ToString() ?? "";
                            result.Add((displayName, displayVersion));
                        }
                        catch { /* отдельная битая подветка Uninstall не должна ронять весь обход */ }
                    }
                }
                catch (Exception ex) { AppLogger.Write($"[OfficeInstallationService] Чтение {uninstallKey}: {ex.Message}"); }
            }
            return result;
        }
    }

    /// <summary>
    /// Определение Office, установленного на машине — любого найденного, не только
    /// поставленного через Ven4Tools (см. docs/superpowers/specs/2026-09-10-
    /// office-uninstall-and-version-change-design.md). C2R приоритетнее MSI: если
    /// ClickToRun\Configuration есть — MSI-записи Uninstall (в т.ч. созданные самим
    /// C2R) игнорируются, чтобы не задваивать и не переключать Kind ошибочно.
    /// </summary>
    public sealed class OfficeInstallationService : IOfficeInstallationDetector
    {
        // MSI-ветка ловит "2013 и старше" (по спеке) в первую очередь, но фильтр
        // намеренно не привязан к конкретному году: как только эта ветка вообще
        // выполняется, C2R уже точно не найден, и любая реальная запись
        // "Microsoft Office ..." в Uninstall — это и есть искомая классическая
        // MSI-установка, год в имени не меняет то, что с ней нужно делать.
        private static readonly Regex OfficeMsiNamePattern =
            new(@"^Microsoft Office\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly IOfficeRegistryReader _registry;

        public OfficeInstallationService() : this(new RealOfficeRegistryReader())
        {
        }

        internal OfficeInstallationService(IOfficeRegistryReader registry)
        {
            _registry = registry;
        }

        public InstalledOfficeInfo Detect()
        {
            var c2r = _registry.ReadClickToRunConfiguration();
            if (c2r.TryGetValue("ProductReleaseIds", out string? rawIds) && !string.IsNullOrWhiteSpace(rawIds))
            {
                var productIds = rawIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                // "," или " , " проходят IsNullOrWhiteSpace, но после RemoveEmptyEntries
                // дают пустой массив — тогда DescribeC2R([0]) упадёт, а Kind = ClickToRun
                // с пустым ProductIds нарушит инвариант задачи (адресное удаление должно
                // быть доступно всегда, когда Kind == ClickToRun). Считаем это "C2R не
                // сконфигурирован" и проваливаемся в MSI/NotFound, как будто ключа не было.
                if (productIds.Length > 0)
                {
                    return new InstalledOfficeInfo
                    {
                        Kind = OfficeInstallationKind.ClickToRun,
                        DisplayName = DescribeC2R(productIds),
                        Platform = c2r.GetValueOrDefault("Platform", ""),
                        Culture = c2r.GetValueOrDefault("ClientCulture", ""),
                        Version = c2r.GetValueOrDefault("VersionToReport", ""),
                        ProductIds = productIds
                    };
                }
            }

            var msiCandidates = _registry.ReadUninstallDisplayEntries()
                .Where(e => OfficeMsiNamePattern.IsMatch(e.DisplayName))
                .ToList();

            if (msiCandidates.Count > 0)
            {
                // Набор кандидатов (и то, что Kind вообще станет Msi) определяется только
                // OfficeMsiNamePattern выше и не меняется ниже. Среди них Uninstall может
                // содержать не только сам пакет, но и его компоненты — языковые пакеты,
                // корректор, общие компоненты и т.п. — которые тоже начинаются с
                // "Microsoft Office" и раньше могли попасть в DisplayName/Version первыми
                // просто по порядку перечисления реестра. Здесь мы лишь ВЫБИРАЕМ, какая
                // из уже отобранных записей похожа на сам пакет, а не на его компонент;
                // если ни одна не похожа — берём первую, как и раньше.
                var suiteEntry = msiCandidates.FirstOrDefault(e => !LooksLikeOfficeComponent(e.DisplayName));
                var msiEntry = suiteEntry.DisplayName != null ? suiteEntry : msiCandidates[0];

                return new InstalledOfficeInfo
                {
                    Kind = OfficeInstallationKind.Msi,
                    DisplayName = msiEntry.DisplayName,
                    Version = msiEntry.DisplayVersion
                };
            }

            return InstalledOfficeInfo.None;
        }

        // Дружелюбное имя для известных ProductReleaseIds (те же 5, что устанавливает
        // сама вкладка — см. OfficeViewModel.ResolveVersion). Незнакомый ID — например,
        // Visio/Project вместе с основным пакетом, или SKU, который Ven4Tools не
        // устанавливает, — показываем как есть, не выдумывая название.
        private static string DescribeC2R(IReadOnlyList<string> productIds)
        {
            string primary = productIds[0];
            string friendly = primary switch
            {
                "O365ProPlusRetail"          => "Office 365 ProPlus",
                "ProPlus2024Retail"          => "Office 2024 ProPlus",
                "Professional2021Retail"     => "Office 2021 Professional",
                "Professional2019Retail"     => "Office 2019 Professional",
                "ProPlusRetail"              => "Office 2016 Professional",
                _                            => primary
            };
            return productIds.Count > 1 ? $"{friendly} (+{productIds.Count - 1})" : friendly;
        }

        // Маркеры, по которым запись "Microsoft Office ..." в Uninstall похожа на
        // отдельный компонент пакета, а не на сам пакет (суть). Список не исчерпывающий —
        // это эвристика предпочтения между уже отобранными кандидатами, а не фильтр
        // допуска: он никогда не решает, детектируется ли Msi вообще (см. комментарий
        // в Detect()).
        private static readonly string[] ComponentMarkers =
        {
            "Proofing Tools", "MUI", "Shared", "Components", "Tools", "Runtime", "Add-in"
        };

        private static bool LooksLikeOfficeComponent(string displayName) =>
            ComponentMarkers.Any(marker => displayName.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Абстракция для OfficeViewModel (Task 3) — тестируется без реального реестра.</summary>
    public interface IOfficeInstallationDetector
    {
        InstalledOfficeInfo Detect();
    }
}
