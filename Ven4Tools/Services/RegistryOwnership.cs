using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Win32;
using Ven4Tools.Helpers;

namespace Ven4Tools.Services
{
    /// <summary>
    /// Значение реестра «как есть»: вид и данные, побайтно. Данные хранятся строкой:
    /// DWORD/QWORD — числом, двоичное — шестнадцатеричной строкой, многострочное — через \0.
    /// </summary>
    public sealed record RegistryRaw(bool Exists, string Kind, string Data)
    {
        public static RegistryRaw Missing { get; } = new(false, "", "");

        public static RegistryRaw From((RegistryValueKind Kind, object Data)? value)
        {
            if (value == null) return Missing;
            var data = value.Value.Data switch
            {
                byte[] b => Convert.ToHexString(b),
                int i => i.ToString(CultureInfo.InvariantCulture),
                long l => l.ToString(CultureInfo.InvariantCulture),
                string[] lines => string.Join("\0", lines),
                var o => Convert.ToString(o, CultureInfo.InvariantCulture) ?? "",
            };
            return new RegistryRaw(true, value.Value.Kind.ToString(), data);
        }

        /// <summary>Значение есть и это DWORD.</summary>
        public bool TryGetDword(out int value)
        {
            value = 0;
            return Exists && Kind == nameof(RegistryValueKind.DWord)
                && int.TryParse(Data, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>
        /// Вид и данные для записи; null — значения не было (его надо удалить). Неизвестный
        /// вид — исключение: файл лежит в профиле пользователя, писать по нему «что угодно» нельзя.
        /// </summary>
        public (RegistryValueKind Kind, object Data)? ToValue()
        {
            if (!Exists) return null;
            var kind = Enum.Parse<RegistryValueKind>(Kind);
            object data = kind switch
            {
                RegistryValueKind.Binary => Convert.FromHexString(Data),
                RegistryValueKind.DWord => int.Parse(Data, CultureInfo.InvariantCulture),
                RegistryValueKind.QWord => long.Parse(Data, CultureInfo.InvariantCulture),
                RegistryValueKind.MultiString => Data.Length == 0 ? Array.Empty<string>() : Data.Split('\0'),
                RegistryValueKind.String or RegistryValueKind.ExpandString => Data,
                _ => throw new InvalidDataException("Неподдерживаемый вид значения реестра: " + Kind),
            };
            return (kind, data);
        }
    }

    /// <summary>
    /// Общий учёт владения значениями реестра между разделами («Очистка», «Панель задач и
    /// Пуск», тема панели). Разделы меняют одни и те же значения (BingSearchEnabled,
    /// DisableSearchBoxSuggestions, ContentDeliveryManager…, ShowCopilotButton и т.п.), и у
    /// каждого своя запись отката. Без общего учёта итог зависел от порядка: второй раздел
    /// снимал «как было» уже с правки первого, и откат мог «вернуть» значение соседа.
    ///
    /// Правило:
    /// <list type="bullet">
    /// <item>первый раздел, взявший значение, запоминает настоящий оригинал пользователя;
    /// следующие получают оригинал отсюда, а не из реестра;</item>
    /// <item>откат раздела, пока значение держит кто-то ещё, реестр не трогает — только
    /// снимает своё владение;</item>
    /// <item>последний владелец возвращает оригинал (побайтно, с видом значения или его отсутствием).</item>
    /// </list>
    ///
    /// Файл: <c>%LOCALAPPDATA%\Ven4Tools\registry_owners.json</c>, ключ «hive\раздел|имя»
    /// (в нижнем регистре) → оригинал + список владельцев («debloat:ads_search_web»,
    /// «taskbar:Bing», «theme»). Запись атомарная (<see cref="FileHelper"/>), под общим замком.
    /// </summary>
    public sealed class RegistryOwnership
    {
        public static RegistryOwnership Default { get; } = new(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ven4Tools", "registry_owners.json"));

        /// <summary>Один замок на все экземпляры: разделы работают из разных потоков.</summary>
        private static readonly object Gate = new();

        private readonly string _path;

        public RegistryOwnership(string path) => _path = path;

        public string FilePath => _path;

        public sealed class Entry
        {
            public RegistryRaw Original { get; set; } = RegistryRaw.Missing;
            public List<string> Owners { get; set; } = new();
        }

        /// <summary>
        /// Ключ значения без оглядки на запись пути: «HKCU:\SOFTWARE\…» («Очистка»),
        /// «HKCU\…» и относительный «Software\…» (разделы панели задач, это HKCU) дают одно и то же.
        /// </summary>
        public static string ValueKey(string path, string name)
        {
            string hive = "hkcu", sub = path;
            foreach (var (prefix, h) in new[]
            {
                (@"HKEY_CURRENT_USER\", "hkcu"), (@"HKEY_LOCAL_MACHINE\", "hklm"),
                (@"HKCU:\", "hkcu"), (@"HKLM:\", "hklm"), (@"HKCU\", "hkcu"), (@"HKLM\", "hklm"),
            })
            {
                if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { hive = h; sub = path[prefix.Length..]; break; }
            }
            return hive + "\\" + sub.Trim('\\').ToLowerInvariant() + "|" + name.ToLowerInvariant();
        }

        /// <summary>
        /// Взять значение во владение перед первой правкой. Возвращает настоящий оригинал:
        /// из учёта, если значение уже держит другой раздел, иначе — текущее значение из реестра.
        /// </summary>
        public RegistryRaw Claim(string path, string name, string owner, Func<RegistryRaw> readCurrent)
        {
            lock (Gate)
            {
                var all = Load(out bool readable);
                var key = ValueKey(path, name);
                if (all.TryGetValue(key, out var entry) && entry.Owners.Count > 0)
                {
                    if (!entry.Owners.Contains(owner, StringComparer.OrdinalIgnoreCase)) entry.Owners.Add(owner);
                }
                else
                {
                    entry = new Entry { Original = readCurrent(), Owners = { owner } };
                    all[key] = entry;
                }
                if (readable) Save(all);
                return entry.Original;
            }
        }

        /// <summary>Значение держит кто-то, кроме <paramref name="owner"/>.</summary>
        public bool HeldByOthers(string path, string name, string owner)
        {
            lock (Gate)
            {
                return Load(out _).TryGetValue(ValueKey(path, name), out var entry)
                    && entry.Owners.Any(o => !string.Equals(o, owner, StringComparison.OrdinalIgnoreCase));
            }
        }

        /// <summary>Значение держит хоть кто-то.</summary>
        public bool IsHeld(string path, string name)
        {
            lock (Gate) return Load(out _).TryGetValue(ValueKey(path, name), out var entry) && entry.Owners.Count > 0;
        }

        /// <summary>Оригинал из учёта; null — значения в учёте нет (откат по своей записи).</summary>
        public RegistryRaw? Original(string path, string name)
        {
            lock (Gate) return Load(out _).TryGetValue(ValueKey(path, name), out var entry) ? entry.Original : null;
        }

        /// <summary>Снять своё владение (после отката). Последний владелец убирает запись совсем.</summary>
        public void Release(string path, string name, string owner)
        {
            lock (Gate)
            {
                var all = Load(out bool readable);
                if (!readable) return;
                var key = ValueKey(path, name);
                if (!all.TryGetValue(key, out var entry)) return;
                entry.Owners.RemoveAll(o => string.Equals(o, owner, StringComparison.OrdinalIgnoreCase));
                if (entry.Owners.Count == 0) all.Remove(key);
                Save(all);
            }
        }

        /// <summary>
        /// Сверка с записями отката раздела: владельцы с префиксом раздела, которых в его
        /// записях уже нет (файл отката удалён, сбой между учётом и записью), снимаются —
        /// иначе они навсегда «держали» бы значение и соседний откат его не вернул бы.
        /// </summary>
        public void Reconcile(string ownerPrefix, IEnumerable<string> liveOwners)
        {
            var live = new HashSet<string>(liveOwners, StringComparer.OrdinalIgnoreCase);
            lock (Gate)
            {
                var all = Load(out bool readable);
                if (!readable) return;
                bool changed = false;
                foreach (var (key, entry) in all.ToList())
                {
                    int removed = entry.Owners.RemoveAll(o =>
                        o.StartsWith(ownerPrefix, StringComparison.OrdinalIgnoreCase) && !live.Contains(o));
                    if (removed == 0) continue;
                    changed = true;
                    if (entry.Owners.Count == 0) all.Remove(key);
                }
                if (changed) Save(all);
            }
        }

        private Dictionary<string, Entry> Load(out bool readable)
        {
            readable = true;
            try
            {
                if (!File.Exists(_path)) return new(StringComparer.OrdinalIgnoreCase);
                var data = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_path));
                return data == null
                    ? new(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, Entry>(data.Where(p => p.Value?.Original != null), StringComparer.OrdinalIgnoreCase);
            }
            catch (JsonException ex)
            {
                // Учёт владения испорчен: откладываем и продолжаем без него — разделы
                // откатываются по своим записям, как до общего учёта.
                var bad = FileHelper.SetAsideBroken(_path);
                AppLogger.Write($"⚠️ Учёт владения реестром испорчен ({ex.Message}) и отложен как {bad ?? "— не отложен"}");
                if (bad == null) readable = false;
                return new(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                AppLogger.Write("⚠️ Учёт владения реестром не прочитан — " + ex.Message);
                readable = false;
                return new(StringComparer.OrdinalIgnoreCase);
            }
        }

        private void Save(Dictionary<string, Entry> all)
        {
            try
            {
                FileHelper.WriteAllTextAtomic(_path, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                AppLogger.Write("⚠️ Учёт владения реестром не сохранён — " + ex.Message);
            }
        }
    }
}
