using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Ven4Tools.Services.Diagnostics
{
    public enum Win11CheckStatus { Pass, Warning, Fail, Unknown }

    public enum FirmwareKind { Unknown, Bios, Uefi }

    /// <summary>Один пункт чеклиста «Готов ли ПК к Windows 11».</summary>
    internal sealed class Win11CheckResult
    {
        public required string Id { get; init; }
        public required string Title { get; init; }
        public required Win11CheckStatus Status { get; init; }

        /// <summary>Что найдено на этом ПК.</summary>
        public required string Detail { get; init; }

        /// <summary>Как исправить; пусто, если всё в порядке.</summary>
        public string Hint { get; init; } = "";
    }

    /// <summary>Видеоадаптер по отчёту dxdiag.</summary>
    internal sealed class DisplayAdapterInfo
    {
        public required string Name { get; init; }
        public Version? Wddm { get; init; }
        public int? DdiMajor { get; init; }
    }

    /// <summary>Сырые сведения о ПК — то, что собрал <see cref="Win11ReadinessService"/>. null — не удалось узнать.</summary>
    internal sealed class Win11RawFacts
    {
        public bool? TpmPresent { get; init; }
        public bool? TpmEnabled { get; init; }
        public bool? TpmActivated { get; init; }
        public string? TpmSpecVersion { get; init; }
        public FirmwareKind Firmware { get; init; }
        public int? SecureBootEnabled { get; init; }
        public int? PartitionStyle { get; init; }
        public ulong? SystemDiskBytes { get; init; }
        public string? CpuName { get; init; }
        public bool? Popcnt { get; init; }
        public bool? Sse42 { get; init; }
        public ulong? InstalledRamKb { get; init; }
        public IReadOnlyList<DisplayAdapterInfo>? Adapters { get; init; }
    }

    internal enum Win11VerdictKind { Ready, ReadyWithUnknowns, NotReady }

    internal sealed class Win11Verdict
    {
        public required Win11VerdictKind Kind { get; init; }
        public required int FailCount { get; init; }
        public required int WarningCount { get; init; }
        public required int UnknownCount { get; init; }
    }

    /// <summary>
    /// Требования Windows 11 и их проверка по собранным сведениям. Только чистая логика:
    /// ни реестра, ни WMI — всё это проверяется юнит-тестами.
    /// Источник требований: https://learn.microsoft.com/windows/whats-new/windows-11-requirements
    /// </summary>
    internal static class Win11Requirements
    {
        internal const string CpuListUrlIntel = "https://learn.microsoft.com/windows-hardware/design/minimum/supported/windows-11-supported-intel-processors";

        /// <summary>4 ГБ; небольшой допуск — часть памяти может резервировать прошивка.</summary>
        internal const ulong MinRamKb = 4UL * 1024 * 1024 - 128 * 1024;

        /// <summary>64 ГБ в десятичных гигабайтах, как на коробке накопителя, с допуском 2 %.</summary>
        internal const ulong MinDiskBytes = 62_720_000_000UL;

        public static IReadOnlyList<Win11CheckResult> Evaluate(Win11RawFacts f, CpuMatchResult cpu) => new[]
        {
            EvaluateTpm(f.TpmPresent, f.TpmEnabled, f.TpmActivated, f.TpmSpecVersion),
            EvaluateSecureBoot(f.Firmware, f.SecureBootEnabled),
            EvaluateFirmware(f.Firmware),
            EvaluatePartitionStyle(f.PartitionStyle),
            EvaluateCpu(cpu),
            EvaluateInstructions(f.Popcnt, f.Sse42),
            EvaluateRam(f.InstalledRamKb),
            EvaluateDisk(f.SystemDiskBytes),
            EvaluateDirectX(f.Adapters)
        };

        /// <summary>«2.0, 0, 1.38» → 2.0; «1.2, 2, 3» → 1.2.</summary>
        internal static Version? ParseTpmSpecMajor(string? spec)
        {
            if (string.IsNullOrWhiteSpace(spec)) return null;
            string first = spec.Split(',')[0].Trim();
            return Version.TryParse(first.Contains('.') ? first : first + ".0", out var v) ? v : null;
        }

        public static Win11CheckResult EvaluateTpm(bool? present, bool? enabled, bool? activated, string? specVersion)
        {
            const string title = "Модуль TPM 2.0";
            const string hintEnable = "TPM чаще всего встроен в процессор, но выключен в настройках. Зайдите в BIOS/UEFI (при включении ПК — Del или F2) и включите Intel PTT (Platform Trust Technology) или AMD fTPM; пункт может называться «Security Device Support» или «TPM Device». Сохраните настройки и перезагрузитесь.";

            if (present == null)
                return new Win11CheckResult { Id = "tpm", Title = title, Status = Win11CheckStatus.Unknown,
                    Detail = "Нет данных: не удалось опросить TPM.", Hint = "Проверьте вручную: Win+R → tpm.msc." };
            if (present == false)
                return new Win11CheckResult { Id = "tpm", Title = title, Status = Win11CheckStatus.Fail,
                    Detail = "TPM не найден.", Hint = hintEnable };

            var version = ParseTpmSpecMajor(specVersion);
            if (version != null && version.Major < 2)
                return new Win11CheckResult { Id = "tpm", Title = title, Status = Win11CheckStatus.Fail,
                    Detail = $"Найден TPM версии {version.Major}.{version.Minor}, нужна 2.0.",
                    Hint = "Если в BIOS/UEFI есть Intel PTT или AMD fTPM — включите его вместо модуля 1.2. Для части плат производитель выпускал обновление прошивки TPM до 2.0." };
            if (enabled == false || activated == false)
                return new Win11CheckResult { Id = "tpm", Title = title, Status = Win11CheckStatus.Fail,
                    Detail = "TPM есть, но выключен или не активирован.", Hint = hintEnable };
            if (version == null)
                return new Win11CheckResult { Id = "tpm", Title = title, Status = Win11CheckStatus.Unknown,
                    Detail = "TPM найден, но версию узнать не удалось.", Hint = "Проверьте вручную: Win+R → tpm.msc, строка «Версия спецификации»." };

            return new Win11CheckResult { Id = "tpm", Title = title, Status = Win11CheckStatus.Pass,
                Detail = $"TPM {version.Major}.{version.Minor}, включён." };
        }

        public static Win11CheckResult EvaluateSecureBoot(FirmwareKind firmware, int? secureBootEnabled)
        {
            const string title = "Безопасная загрузка (Secure Boot)";
            if (firmware == FirmwareKind.Bios)
                return new Win11CheckResult { Id = "secureboot", Title = title, Status = Win11CheckStatus.Fail,
                    Detail = "Недоступна: ПК загружается в режиме BIOS (Legacy).",
                    Hint = "Secure Boot работает только в режиме UEFI. Сначала переведите загрузку в UEFI (см. пункты «Режим загрузки» и «Разметка системного диска»), затем включите Secure Boot в BIOS/UEFI." };
            if (secureBootEnabled == 1)
                return new Win11CheckResult { Id = "secureboot", Title = title, Status = Win11CheckStatus.Pass, Detail = "Включена." };
            if (secureBootEnabled == 0)
                return new Win11CheckResult { Id = "secureboot", Title = title, Status = Win11CheckStatus.Warning,
                    Detail = "Поддерживается, но выключена.",
                    Hint = "Windows 11 требует, чтобы ПК поддерживал Secure Boot, — это так. Включить её можно в BIOS/UEFI: раздел Boot или Security → Secure Boot → Enabled (иногда сначала нужно выбрать «Windows UEFI mode» и выключить CSM)." };
            return new Win11CheckResult { Id = "secureboot", Title = title, Status = Win11CheckStatus.Unknown,
                Detail = "Нет данных: Windows не сообщает состояние Secure Boot.", Hint = "Проверьте вручную: Win+R → msinfo32, строка «Состояние безопасной загрузки»." };
        }

        public static Win11CheckResult EvaluateFirmware(FirmwareKind firmware)
        {
            const string title = "Режим загрузки UEFI";
            return firmware switch
            {
                FirmwareKind.Uefi => new Win11CheckResult { Id = "uefi", Title = title, Status = Win11CheckStatus.Pass, Detail = "UEFI." },
                FirmwareKind.Bios => new Win11CheckResult { Id = "uefi", Title = title, Status = Win11CheckStatus.Fail,
                    Detail = "Устаревший режим BIOS (Legacy / CSM).",
                    Hint = "Порядок важен: сначала преобразуйте системный диск в GPT (пункт ниже), и только потом в BIOS/UEFI выключите CSM/Legacy и включите UEFI. Если переключить режим раньше, Windows перестанет загружаться." },
                _ => new Win11CheckResult { Id = "uefi", Title = title, Status = Win11CheckStatus.Unknown,
                    Detail = "Нет данных.", Hint = "Проверьте вручную: Win+R → msinfo32, строка «Режим BIOS»." }
            };
        }

        public static Win11CheckResult EvaluatePartitionStyle(int? partitionStyle)
        {
            const string title = "Разметка системного диска GPT";
            return partitionStyle switch
            {
                2 => new Win11CheckResult { Id = "gpt", Title = title, Status = Win11CheckStatus.Pass, Detail = "GPT." },
                1 => new Win11CheckResult { Id = "gpt", Title = title, Status = Win11CheckStatus.Fail,
                    Detail = "MBR.",
                    Hint = "Диск можно преобразовать в GPT без потери данных встроенной утилитой Windows mbr2gpt: в командной строке от имени администратора сначала «mbr2gpt /validate /allowFullOS», и если проверка прошла — «mbr2gpt /convert /allowFullOS». Сразу после этого в BIOS/UEFI включите режим UEFI. Перед преобразованием сделайте резервную копию важных файлов. Ven4Tools эту команду не запускает." },
                _ => new Win11CheckResult { Id = "gpt", Title = title, Status = Win11CheckStatus.Unknown,
                    Detail = "Нет данных: не удалось определить системный диск.", Hint = "Проверьте вручную: «Управление дисками» → свойства диска → вкладка «Тома» → «Стиль раздела»." }
            };
        }

        public static Win11CheckResult EvaluateCpu(CpuMatchResult cpu)
        {
            const string title = "Процессор в списке Microsoft";
            string name = cpu.NormalizedName.Length > 0 ? cpu.NormalizedName : "неизвестный процессор";
            return cpu.Support switch
            {
                CpuSupport.Supported => new Win11CheckResult { Id = "cpu", Title = title, Status = Win11CheckStatus.Pass,
                    Detail = $"{name} — есть в списке." },
                CpuSupport.NotSupported => new Win11CheckResult { Id = "cpu", Title = title, Status = Win11CheckStatus.Fail,
                    Detail = $"{name} — нет в списке.",
                    Hint = "Это свойство самого процессора: настройками не исправить, только заменой процессора (часто вместе с платой). Windows 10 при этом продолжит работать — см. блок «Ваша Windows» ниже." },
                _ => new Win11CheckResult { Id = "cpu", Title = title, Status = Win11CheckStatus.Unknown,
                    Detail = $"Нет данных: модель «{name}» не удалось сопоставить со списком.",
                    Hint = "Сверьтесь со списком Microsoft вручную (кнопка «Списки процессоров Microsoft»)." }
            };
        }

        public static Win11CheckResult EvaluateInstructions(bool? popcnt, bool? sse42)
        {
            const string title = "Инструкции POPCNT и SSE4.2 (нужны с 24H2)";
            if (popcnt == null || sse42 == null)
                return new Win11CheckResult { Id = "popcnt", Title = title, Status = Win11CheckStatus.Unknown,
                    Detail = "Нет данных: проверка доступна только для процессоров x86/x64." };
            if (popcnt == true && sse42 == true)
                return new Win11CheckResult { Id = "popcnt", Title = title, Status = Win11CheckStatus.Pass, Detail = "Обе поддерживаются." };

            string missing = popcnt == false && sse42 == false ? "POPCNT и SSE4.2" : popcnt == false ? "POPCNT" : "SSE4.2";
            return new Win11CheckResult { Id = "popcnt", Title = title, Status = Win11CheckStatus.Fail,
                Detail = $"Не поддерживается: {missing}.",
                Hint = "Без этих инструкций Windows 11 24H2 и новее не запускается вообще, даже в обход проверок. Это свойство самого процессора, настройками не исправить." };
        }

        public static Win11CheckResult EvaluateRam(ulong? installedKb)
        {
            const string title = "Оперативная память от 4 ГБ";
            if (installedKb == null)
                return new Win11CheckResult { Id = "ram", Title = title, Status = Win11CheckStatus.Unknown, Detail = "Нет данных." };
            string gb = (installedKb.Value / 1024.0 / 1024.0).ToString("0.#", CultureInfo.CurrentCulture);
            return installedKb.Value >= MinRamKb
                ? new Win11CheckResult { Id = "ram", Title = title, Status = Win11CheckStatus.Pass, Detail = $"{gb} ГБ." }
                : new Win11CheckResult { Id = "ram", Title = title, Status = Win11CheckStatus.Fail, Detail = $"{gb} ГБ.",
                    Hint = "Нужно минимум 4 ГБ — добавьте или замените модуль памяти." };
        }

        public static Win11CheckResult EvaluateDisk(ulong? diskBytes)
        {
            const string title = "Системный диск от 64 ГБ";
            if (diskBytes == null)
                return new Win11CheckResult { Id = "disk", Title = title, Status = Win11CheckStatus.Unknown, Detail = "Нет данных: не удалось определить системный диск." };
            string gb = (diskBytes.Value / 1_000_000_000.0).ToString("0", CultureInfo.CurrentCulture);
            return diskBytes.Value >= MinDiskBytes
                ? new Win11CheckResult { Id = "disk", Title = title, Status = Win11CheckStatus.Pass, Detail = $"{gb} ГБ." }
                : new Win11CheckResult { Id = "disk", Title = title, Status = Win11CheckStatus.Fail, Detail = $"{gb} ГБ.",
                    Hint = "Нужен накопитель от 64 ГБ, а для спокойной работы с обновлениями — заметно больше. Поможет только замена диска." };
        }

        public static Win11CheckResult EvaluateDirectX(IReadOnlyList<DisplayAdapterInfo>? adapters)
        {
            const string title = "Видеокарта: DirectX 12 и драйвер WDDM 2.0";
            const string hintDriver = "Установите или обновите драйвер видеокарты с сайта её производителя и повторите проверку: без драйвера Windows видит только «Базовый видеоадаптер».";
            if (adapters == null || adapters.Count == 0)
                return new Win11CheckResult { Id = "dx12", Title = title, Status = Win11CheckStatus.Unknown,
                    Detail = "Нет данных: средство диагностики DirectX не ответило.", Hint = "Проверьте вручную: Win+R → dxdiag, вкладка «Экран»." };

            var good = adapters.FirstOrDefault(a => a.DdiMajor >= 12 && a.Wddm != null && a.Wddm.Major >= 2);
            if (good != null)
                return new Win11CheckResult { Id = "dx12", Title = title, Status = Win11CheckStatus.Pass,
                    Detail = $"{good.Name}: DirectX {good.DdiMajor}, WDDM {good.Wddm!.Major}.{good.Wddm.Minor}." };

            // Хотя бы у одного адаптера всё известно — значит, честно «не подходит».
            var known = adapters.FirstOrDefault(a => a.DdiMajor != null && a.Wddm != null);
            if (known != null)
                return new Win11CheckResult { Id = "dx12", Title = title, Status = Win11CheckStatus.Fail,
                    Detail = $"{known.Name}: DirectX {known.DdiMajor}, WDDM {known.Wddm!.Major}.{known.Wddm.Minor}.",
                    Hint = "Сначала обновите видеодрайвер с сайта производителя — старый драйвер часто сообщает более раннюю версию WDDM. Если не поможет, нужна видеокарта с поддержкой DirectX 12." };

            return new Win11CheckResult { Id = "dx12", Title = title, Status = Win11CheckStatus.Unknown,
                Detail = $"Нет данных по адаптеру «{adapters[0].Name}».", Hint = hintDriver };
        }

        private static readonly Regex WddmRegex = new(@"WDDM\s*(?<v>\d+(?:\.\d+)?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex FirstNumberRegex = new(@"\d+", RegexOptions.CultureInvariant);

        /// <summary>
        /// Разбор отчёта «dxdiag /x»: DxDiag/DisplayDevices/DisplayDevice с полями CardName,
        /// DriverModel («WDDM 3.1») и DDIVersion («12»). Имена элементов XML не переводятся,
        /// значения «Unknown»/«Неизвестно» дают null. Битый XML — null.
        /// </summary>
        internal static List<DisplayAdapterInfo>? ParseDxDiagXml(string? xml)
        {
            if (string.IsNullOrWhiteSpace(xml)) return null;
            XDocument doc;
            try { doc = XDocument.Parse(xml); }
            catch (System.Xml.XmlException) { return null; }
            return ParseDxDiag(doc);
        }

        /// <summary>То же для уже загруженного документа (файл dxdiag читается через XDocument.Load —
        /// так кодировку из заголовка XML и BOM разбирает сам XML-парсер).</summary>
        internal static List<DisplayAdapterInfo> ParseDxDiag(XDocument doc)
        {
            var list = new List<DisplayAdapterInfo>();
            foreach (var device in doc.Descendants("DisplayDevice"))
            {
                string name = device.Element("CardName")?.Value.Trim() ?? "";
                if (name.Length == 0) name = device.Element("ChipType")?.Value.Trim() ?? "?";

                Version? wddm = null;
                var wm = WddmRegex.Match(device.Element("DriverModel")?.Value ?? "");
                if (wm.Success)
                {
                    string v = wm.Groups["v"].Value;
                    Version.TryParse(v.Contains('.') ? v : v + ".0", out wddm);
                }

                int? ddi = null;
                var dm = FirstNumberRegex.Match(device.Element("DDIVersion")?.Value ?? "");
                if (dm.Success && int.TryParse(dm.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int d)) ddi = d;

                list.Add(new DisplayAdapterInfo { Name = name, Wddm = wddm, DdiMajor = ddi });
            }
            return list;
        }

        public static Win11Verdict ComputeVerdict(IEnumerable<Win11CheckResult> checks)
        {
            var list = checks.ToList();
            int fail = list.Count(c => c.Status == Win11CheckStatus.Fail);
            int warn = list.Count(c => c.Status == Win11CheckStatus.Warning);
            int unknown = list.Count(c => c.Status == Win11CheckStatus.Unknown);
            var kind = fail > 0 ? Win11VerdictKind.NotReady
                : unknown > 0 ? Win11VerdictKind.ReadyWithUnknowns
                : Win11VerdictKind.Ready;
            return new Win11Verdict { Kind = kind, FailCount = fail, WarningCount = warn, UnknownCount = unknown };
        }
    }
}
