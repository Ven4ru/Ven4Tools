using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Management;
using System.Text;

namespace Ven4Tools.Launcher.Services
{
    /// <summary>Что, по всем признакам, случилось с файлами клиента.</summary>
    internal enum FileProblemKind
    {
        /// <summary>Проблемы нет (файл на месте, ошибки записи нет).</summary>
        None,
        /// <summary>Нет прав на запись в папку (ACL или «Контролируемый доступ к папкам»).</summary>
        AccessDenied,
        /// <summary>Антивирус подтверждён: код «обнаружен вирус» от Windows или запись детекта Defender по нашему пути.</summary>
        AntivirusConfirmed,
        /// <summary>Похоже на антивирус: файл исчез сразу после записи или запись в доступную папку отклонена.</summary>
        AntivirusLikely,
        /// <summary>Признаков недостаточно — честно «не знаем».</summary>
        Unknown
    }

    /// <summary>Признаки, по которым <see cref="AntivirusAdvisor.Classify"/> выносит вердикт.</summary>
    internal sealed class FileProblemEvidence
    {
        /// <summary>Код ошибки Win32 из исключения записи/запуска (или null).</summary>
        public int? Win32Error { get; init; }
        /// <summary>Файл был на диске (мы его положили или видели), а теперь его нет.</summary>
        public bool FileVanished { get; init; }
        /// <summary>Результат пробной записи в папку: true — пишется, false — отказ, null — не проверяли.</summary>
        public bool? FolderWritable { get; init; }
        /// <summary>У Defender есть запись детекта по пути внутри папки клиента.</summary>
        public bool DefenderDetectionMatched { get; init; }
    }

    /// <summary>Антивирус, зарегистрированный в Центре безопасности Windows.</summary>
    internal sealed record AntivirusProduct(string Name, bool? Enabled);

    /// <summary>Запись детекта Defender по файлу в нашей папке.</summary>
    internal sealed record DefenderDetection(string ThreatName, string Resource, DateTime? DetectedAt);

    /// <summary>Ссылка для пользователя: подпись и адрес (только https, фиксированные сайты).</summary>
    internal sealed record AdviceLink(string Title, string Url);

    /// <summary>
    /// Л3 «Похоже, файл удалил антивирус». Только ЧТЕНИЕ: какой антивирус стоит
    /// (root\SecurityCenter2\AntiVirusProduct), что нашёл Defender по нашему пути
    /// (root\Microsoft\Windows\Defender: MSFT_MpThreatDetection / MSFT_MpThreat), можно ли
    /// писать в папку. Исключения в антивирус лаунчер НЕ добавляет никогда и ни при каких
    /// условиях: так ведут себя вредоносные программы, и антивирус был бы прав, пометив
    /// после этого сам лаунчер. Пользователю — объяснение, хеш файла и куда сообщить о
    /// ложном срабатывании.
    ///
    /// Классификация и тексты — чистые функции (покрыты unit-тестами); WMI и пробная
    /// запись — отдельными методами, любой их сбой = «нет данных», а не «всё хорошо».
    /// </summary>
    internal static class AntivirusAdvisor
    {
        // Коды Win32, по которым различаем причины.
        internal const int ErrorFileNotFound = 2;
        internal const int ErrorPathNotFound = 3;
        internal const int ErrorAccessDenied = 5;
        internal const int ErrorVirusInfected = 225;   // ERROR_VIRUS_INFECTED: «файл содержит вирус»
        internal const int ErrorVirusDeleted = 226;    // ERROR_VIRUS_DELETED: «файл удалён как вирус»

        /// <summary>
        /// Код Win32 из исключения: Win32Exception.NativeErrorCode либо HRESULT вида
        /// 0x8007xxxx (так .NET заворачивает ошибки файловой системы). Ищет и во
        /// вложенных исключениях. Чистая функция — покрыта unit-тестами.
        /// </summary>
        internal static int? ExtractWin32Error(Exception? ex)
        {
            for (Exception? cur = ex; cur != null; cur = cur.InnerException)
            {
                if (cur is Win32Exception w32) return w32.NativeErrorCode;
                if (cur is UnauthorizedAccessException) return ErrorAccessDenied;
                int hr = cur.HResult;
                if (((uint)hr & 0xFFFF0000u) == 0x80070000u) return hr & 0xFFFF;
            }
            return null;
        }

        /// <summary>
        /// Вердикт по признакам. Порядок важен: прямое слово Windows («вирус») и запись
        /// детекта Defender сильнее косвенных признаков; «нет доступа» — только когда
        /// пробная запись в папку действительно не прошла, иначе отказ в доступной на
        /// запись папке — это кто-то держит или блокирует именно наш файл.
        /// Чистая функция — покрыта unit-тестами.
        /// </summary>
        internal static FileProblemKind Classify(FileProblemEvidence e)
        {
            if (e.Win32Error is ErrorVirusInfected or ErrorVirusDeleted) return FileProblemKind.AntivirusConfirmed;
            if (e.DefenderDetectionMatched) return FileProblemKind.AntivirusConfirmed;

            if (e.FileVanished) return FileProblemKind.AntivirusLikely;

            if (e.Win32Error == ErrorAccessDenied)
            {
                return e.FolderWritable switch
                {
                    false => FileProblemKind.AccessDenied,
                    true => FileProblemKind.AntivirusLikely,
                    null => FileProblemKind.Unknown,
                };
            }

            if (e.Win32Error is ErrorFileNotFound or ErrorPathNotFound) return FileProblemKind.AntivirusLikely;

            return e.Win32Error == null ? FileProblemKind.None : FileProblemKind.Unknown;
        }

        /// <summary>
        /// Пробная запись в папку: создаём и сразу удаляем пустой временный файл.
        /// Папки нет — проверяем ближайшую существующую родительскую (туда и будет
        /// создаваться). null — проверить не удалось ни так, ни так.
        /// </summary>
        internal static bool? ProbeFolderWritable(string? folder)
        {
            try
            {
                string? dir = folder;
                while (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    dir = Path.GetDirectoryName(dir);
                if (string.IsNullOrEmpty(dir)) return null;

                string probe = Path.Combine(dir, $".v4t-write-probe-{Guid.NewGuid():N}.tmp");
                using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
                {
                }
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            catch (IOException ex) when (ExtractWin32Error(ex) == ErrorAccessDenied)
            {
                return false;
            }
            catch (Exception ex)
            {
                LauncherLog.Write($"[Антивирус] Пробная запись в папку не выполнена: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Антивирусы из Центра безопасности Windows. null — список получить не удалось
        /// (нет WMI, Windows Server без SecurityCenter2 и т.п.): это «нет данных», а не
        /// «антивируса нет».
        /// </summary>
        internal static List<AntivirusProduct>? QueryAntivirusProducts()
        {
            try
            {
                var result = new List<AntivirusProduct>();
                using var searcher = new ManagementObjectSearcher(
                    @"root\SecurityCenter2", "SELECT displayName, productState FROM AntiVirusProduct",
                    new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(10), ReturnImmediately = false });
                foreach (ManagementBaseObject mo in searcher.Get())
                {
                    using (mo)
                    {
                        string name = mo["displayName"] as string ?? "";
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        bool? enabled = mo["productState"] is uint state ? IsProductEnabled(state) : null;
                        result.Add(new AntivirusProduct(name.Trim(), enabled));
                    }
                }
                return result;
            }
            catch (Exception ex)
            {
                LauncherLog.Write($"[Антивирус] Список антивирусов не получен: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// productState из SecurityCenter2 — недокументированная битовая маска. Общепринятое
        /// прочтение: второй байт 0x10 — защита включена (0x1000), 0x00/0x01 — выключена.
        /// ПРОВЕРИТЬ НА ВМ (Kaspersky, Dr.Web, выключенный Defender). При сомнении текст
        /// не утверждает, что антивирус выключен, — только перечисляет установленные.
        /// Чистая функция — покрыта unit-тестами.
        /// </summary>
        internal static bool IsProductEnabled(uint productState) => (productState & 0x1000u) != 0;

        /// <summary>
        /// Детекты Defender, у которых путь ресурса лежит внутри <paramref name="folder"/>.
        /// Ресурсы Defender записывает как «file:_C:\путь\файл» (бывает и «containerfile:_…»).
        /// null — запросить не удалось (Defender выключен другим антивирусом, нет прав и т.п.).
        /// </summary>
        internal static List<DefenderDetection>? QueryDefenderDetections(string folder)
        {
            try
            {
                const string ns = @"root\Microsoft\Windows\Defender";
                var options = new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(10), ReturnImmediately = false };
                var names = new Dictionary<long, string>();
                using (var threats = new ManagementObjectSearcher(ns, "SELECT ThreatID, ThreatName FROM MSFT_MpThreat", options))
                {
                    foreach (ManagementBaseObject mo in threats.Get())
                    {
                        using (mo)
                        {
                            if (mo["ThreatID"] is { } id && mo["ThreatName"] is string tn)
                                names[Convert.ToInt64(id, System.Globalization.CultureInfo.InvariantCulture)] = tn;
                        }
                    }
                }

                var result = new List<DefenderDetection>();
                using var detections = new ManagementObjectSearcher(ns,
                    "SELECT ThreatID, Resources, InitialDetectionTime FROM MSFT_MpThreatDetection", options);
                foreach (ManagementBaseObject mo in detections.Get())
                {
                    using (mo)
                    {
                        if (mo["Resources"] is not string[] resources) continue;
                        string? match = resources.FirstOrDefault(r => ResourceIsInFolder(r, folder));
                        if (match == null) continue;
                        long id = mo["ThreatID"] is { } tid ? Convert.ToInt64(tid, System.Globalization.CultureInfo.InvariantCulture) : 0;
                        DateTime? when = null;
                        if (mo["InitialDetectionTime"] is string dmtf)
                        {
                            try { when = ManagementDateTimeConverter.ToDateTime(dmtf); } catch { /* формат даты WMI — не критично */ }
                        }
                        result.Add(new DefenderDetection(names.TryGetValue(id, out var n) ? n : $"ID {id}", match, when));
                    }
                }
                return result;
            }
            catch (Exception ex)
            {
                LauncherLog.Write($"[Антивирус] Журнал обнаружений Defender не прочитан: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Лежит ли ресурс детекта Defender («file:_C:\…», «containerfile:_C:\…») внутри папки.
        /// Сравнение по границе каталога: «C:\Ven4Tools2» не считается внутри «C:\Ven4Tools».
        /// Чистая функция — покрыта unit-тестами.
        /// </summary>
        internal static bool ResourceIsInFolder(string? resource, string? folder)
        {
            if (string.IsNullOrWhiteSpace(resource) || string.IsNullOrWhiteSpace(folder)) return false;
            int sep = resource.IndexOf(":_", StringComparison.Ordinal);
            string path = sep >= 0 ? resource[(sep + 2)..] : resource;
            // У архивов Defender дописывает путь внутри контейнера через «->».
            int arrow = path.IndexOf("->", StringComparison.Ordinal);
            if (arrow >= 0) path = path[..arrow];
            string root = folder.TrimEnd('\\', '/') + "\\";
            return path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(path, folder.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Куда сообщить о ложном срабатывании и где проверить файл. Ссылки
        /// фиксированные: страницы лаборатории антивирусов и VirusTotal ПО ХЕШУ — сам файл
        /// никуда не загружается. Первыми — ссылки для антивирусов, которые реально стоят.
        /// Чистая функция — покрыта unit-тестами.
        /// </summary>
        internal static List<AdviceLink> BuildLinks(string? sha256, IReadOnlyCollection<string>? antivirusNames)
        {
            bool hasHash = DownloadValidator.IsValidSha256(sha256);
            string hash = hasHash ? sha256!.ToLowerInvariant() : "";
            var names = antivirusNames ?? Array.Empty<string>();
            bool Has(string part) => names.Any(n => n.Contains(part, StringComparison.OrdinalIgnoreCase));

            var kaspersky = new AdviceLink("Kaspersky OpenTIP: проверить файл и запросить повторный анализ",
                hasHash ? $"https://opentip.kaspersky.com/{hash}/" : "https://opentip.kaspersky.com/");
            var drweb = new AdviceLink("Dr.Web: сообщить о ложном срабатывании",
                "https://vms.drweb.ru/sendvirus/");
            var microsoft = new AdviceLink("Microsoft: отправить файл на анализ (ложное срабатывание Defender)",
                "https://www.microsoft.com/en-us/wdsi/filesubmission");

            var ordered = new List<AdviceLink>();
            if (Has("Kaspersky") || Has("Касперск")) ordered.Add(kaspersky);
            if (Has("Dr.Web") || Has("DrWeb")) ordered.Add(drweb);
            if (Has("Defender") || Has("Microsoft")) ordered.Add(microsoft);
            foreach (var l in new[] { microsoft, kaspersky, drweb })
                if (!ordered.Contains(l)) ordered.Add(l);

            if (hasHash)
                ordered.Add(new AdviceLink("VirusTotal: результаты проверки по хешу (файл не загружается)",
                    $"https://www.virustotal.com/gui/file/{hash}"));
            return ordered;
        }

        /// <summary>
        /// Заголовок и текст объяснения. Чистая функция — покрыта unit-тестами.
        /// Никогда не советует отключить антивирус или добавить исключение «автоматически»:
        /// только объяснить, проверить и сообщить о ложном срабатывании.
        /// </summary>
        internal static (string Title, string Body) BuildExplanation(
            FileProblemKind kind,
            IReadOnlyList<AntivirusProduct>? products,
            IReadOnlyList<DefenderDetection>? detections,
            string? fileName)
        {
            string file = string.IsNullOrWhiteSpace(fileName) ? "файл клиента" : $"файл {fileName}";
            var sb = new StringBuilder();
            string title;
            switch (kind)
            {
                case FileProblemKind.AccessDenied:
                    title = "Нет доступа к папке клиента";
                    sb.AppendLine("Windows не разрешает записывать в папку клиента. Это права доступа к папке, а не антивирус.");
                    sb.AppendLine();
                    sb.AppendLine("Что можно сделать: выбрать другую папку установки в своём профиле или вернуть себе права на эту папку (свойства папки → Безопасность). " +
                                  "Если папка находится в «Документах» и включён «Контролируемый доступ к папкам» Защитника Windows — он тоже выглядит как «нет доступа».");
                    return (title, sb.ToString().TrimEnd());

                case FileProblemKind.AntivirusConfirmed:
                    title = "Антивирус заблокировал или удалил файл клиента";
                    sb.AppendLine($"Похоже, {file} удалил или заблокировал антивирус.");
                    break;

                case FileProblemKind.AntivirusLikely:
                    title = "Похоже, файл удалил антивирус";
                    sb.AppendLine($"Лаунчер записал {file}, но сейчас его нет на месте или доступ к нему запрещён, хотя писать в папку можно. " +
                                  "Чаще всего так выглядит срабатывание антивируса. Точно подтвердить это лаунчер не может.");
                    break;

                case FileProblemKind.Unknown:
                    title = "Не удалось записать файлы клиента";
                    sb.AppendLine("Причину определить не удалось: это может быть и антивирус, и права доступа к папке.");
                    break;

                default:
                    return ("Проблем не обнаружено", "Файлы клиента на месте.");
            }

            sb.AppendLine();
            if (detections is { Count: > 0 })
            {
                sb.AppendLine("Защитник Windows сообщает об обнаружении в папке клиента:");
                foreach (var d in detections.Take(5))
                    sb.AppendLine($"  • {d.ThreatName}" + (d.DetectedAt is { } t ? $" ({t:dd.MM.yyyy HH:mm})" : ""));
                sb.AppendLine();
            }

            if (products == null)
                sb.AppendLine("Установленный антивирус: нет данных (Центр безопасности Windows не ответил).");
            else if (products.Count == 0)
                sb.AppendLine("Установленный антивирус: Центр безопасности Windows не сообщил ни одного.");
            else
                sb.AppendLine("Установленный антивирус: " + string.Join(", ", products.Select(p =>
                    p.Name + (p.Enabled == false ? " (защита, похоже, выключена)" : ""))) + ".");

            sb.AppendLine();
            sb.AppendLine("Ven4Tools не содержит вредоносного кода, но новые версии без «репутации» иногда принимают за угрозу. " +
                          "Проверьте файл по хешу SHA256 ниже и, если считаете срабатывание ложным, сообщите о нём лаборатории своего антивируса. " +
                          "Лаунчер сам ничего не добавляет в исключения антивируса и не советует его отключать. " +
                          "После ответа лаборатории (обычно от нескольких часов до пары дней) нажмите «Проверить и восстановить клиент».");
            return (title, sb.ToString().TrimEnd());
        }
    }
}
