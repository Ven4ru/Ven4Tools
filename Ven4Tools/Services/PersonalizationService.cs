using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace Ven4Tools.Services
{
    /// <summary>Что входит в оформление «Венчик».</summary>
    public enum PersonalizationPart { Cursors, Wallpaper, LockScreen, Sounds, Screensaver }

    /// <summary>Доступ к реестру и системным настройкам; вынесен интерфейсом ради тестов.</summary>
    public interface IPersonalizationSystem
    {
        /// <summary>Строковое значение реестра; null — значения нет. Путь — «HKCU\…» или «HKLM\…».</summary>
        string? ReadString(string path, string name);
        void WriteString(string path, string name, string value);
        (bool Exists, int Value) ReadDword(string path, string name);
        void WriteDword(string path, string name, int value);
        void DeleteValue(string path, string name);
        /// <summary>Сообщить Windows, что изменились курсоры, обои или заставка.
        /// <paramref name="arg"/> — путь к обоям или «1»/«0» для заставки.</summary>
        void Broadcast(PersonalizationPart part, string? arg);
    }

    /// <summary>
    /// Оформление Windows в стиле Венчика: курсоры, обои, экран блокировки, звуки и заставка.
    ///
    /// Каждый пункт применяется и возвращается по отдельности. Перед первым применением
    /// запоминается всё, что пункт меняет (значения реестра или их отсутствие), — в файл
    /// рядом с остальными настройками. Повторное «Применить» прежнее состояние не
    /// перезаписывает: иначе «как было» стало бы уже оформлением Венчика.
    ///
    /// Файлы оформления лежат в папке программы (<c>Personalization\Venchik</c>) и при
    /// применении копируются в постоянное место: Windows хранит в реестре путь к ним,
    /// а папка программы меняется при обновлении.
    /// </summary>
    public sealed class PersonalizationService
    {
        public const string Cursors = @"HKCU\Control Panel\Cursors";
        public const string Desktop = @"HKCU\Control Panel\Desktop";
        public const string LockScreenCsp = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\PersonalizationCSP";
        public const string SoundScheme = @"HKCU\AppEvents\Schemes";
        public const string SoundEvents = @"HKCU\AppEvents\Schemes\Apps\.Default";

        /// <summary>Курсоры набора: имя значения в реестре → файл.</summary>
        public static readonly IReadOnlyDictionary<string, string> CursorFiles = new Dictionary<string, string>
        {
            ["Arrow"] = "paw-arrow.cur",
            ["Hand"] = "paw-hand.cur",
            ["Help"] = "paw-help.cur",
            ["AppStarting"] = "paw-busy.ani",
            ["Wait"] = "venchik-wait.ani",
        };

        /// <summary>Звуки набора: событие Windows → файл.</summary>
        public static readonly IReadOnlyDictionary<string, string> SoundFiles = new Dictionary<string, string>
        {
            ["SystemAsterisk"] = "info.wav",
            ["SystemExclamation"] = "warning.wav",
            ["SystemHand"] = "error.wav",
            ["SystemNotification"] = "notify.wav",
            ["Notification.Default"] = "notify.wav",
            ["DeviceConnect"] = "connect.wav",
            ["DeviceDisconnect"] = "disconnect.wav",
        };

        public const string WallpaperFile = "wallpaper.jpg";
        public const string LockScreenFile = "lockscreen.jpg";
        public const string ScreensaverFile = "Venchik.scr";
        public const string SchemeName = "Venchik";

        public static PersonalizationService Default { get; } = new(
            Path.Combine(AppContext.BaseDirectory, "Personalization", "Venchik"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ven4Tools", "Personalization"),
            // Экран блокировки рисует системная служба, а не пользователь, — картинку
            // кладём в общую папку, которую она точно может прочитать.
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Ven4Tools", "Personalization"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ven4Tools", "personalization_undo.json"),
            new WindowsPersonalizationSystem());

        private readonly string _source;
        private readonly string _userDir;
        private readonly string _sharedDir;
        private readonly string _undoPath;
        private readonly IPersonalizationSystem _system;
        private readonly object _gate = new();

        public PersonalizationService(string source, string userDir, string sharedDir, string undoPath, IPersonalizationSystem system)
        {
            _source = source;
            _userDir = userDir;
            _sharedDir = sharedDir;
            _undoPath = undoPath;
            _system = system;
        }

        public sealed class ValueState
        {
            public string Path { get; set; } = "";
            public string Name { get; set; } = "";
            public bool Existed { get; set; }
            public string? Text { get; set; }
            /// <summary>Не null — значение было числом (DWORD).</summary>
            public int? Dword { get; set; }
        }

        public sealed class UndoRecord
        {
            public DateTime AppliedUtc { get; set; }
            public List<ValueState> Values { get; set; } = new();
        }

        /// <summary>Файлы набора на месте — раздел можно показывать.</summary>
        public bool IsAvailable(PersonalizationPart part) => SourceFiles(part).All(f => File.Exists(Path.Combine(_source, f)));

        public string? PreviewPath(PersonalizationPart part) => part switch
        {
            PersonalizationPart.Wallpaper => Existing(WallpaperFile),
            PersonalizationPart.LockScreen => Existing(LockScreenFile),
            _ => null,
        };

        private string? Existing(string file)
        {
            var path = Path.Combine(_source, file);
            return File.Exists(path) ? path : null;
        }

        /// <summary>Пункт применён: сохранено прежнее состояние.</summary>
        public bool IsApplied(PersonalizationPart part)
        {
            lock (_gate) return Load().ContainsKey(part.ToString());
        }

        public void Apply(PersonalizationPart part)
        {
            if (!IsAvailable(part))
                throw new FileNotFoundException("Нет файлов оформления: " + string.Join(", ", SourceFiles(part)));

            lock (_gate)
            {
                var all = Load();
                if (!all.ContainsKey(part.ToString()))
                {
                    all[part.ToString()] = new UndoRecord { AppliedUtc = DateTime.UtcNow, Values = Snapshot(part) };
                    Save(all);
                }
            }

            switch (part)
            {
                case PersonalizationPart.Cursors:
                    foreach (var (name, file) in CursorFiles)
                        _system.WriteString(Cursors, name, Copy(file, _userDir));
                    _system.WriteString(Cursors, "", SchemeName);
                    _system.Broadcast(part, null);
                    break;

                case PersonalizationPart.Wallpaper:
                    var wallpaper = Copy(WallpaperFile, _userDir);
                    // «Заполнение»: картинка без полос на экране любой пропорции.
                    _system.WriteString(Desktop, "WallpaperStyle", "10");
                    _system.WriteString(Desktop, "TileWallpaper", "0");
                    _system.Broadcast(part, wallpaper);
                    break;

                case PersonalizationPart.LockScreen:
                    var lockImage = Copy(LockScreenFile, _sharedDir);
                    _system.WriteString(LockScreenCsp, "LockScreenImagePath", lockImage);
                    _system.WriteString(LockScreenCsp, "LockScreenImageUrl", lockImage);
                    _system.WriteDword(LockScreenCsp, "LockScreenImageStatus", 1);
                    break;

                case PersonalizationPart.Sounds:
                    foreach (var (evt, file) in SoundFiles)
                        _system.WriteString(SoundEvents + "\\" + evt + "\\.Current", "", Copy(file, _userDir));
                    _system.WriteString(SoundScheme, "", ".Current");
                    break;

                case PersonalizationPart.Screensaver:
                    var scr = Copy(ScreensaverFile, _userDir);
                    // Старые части Windows запускают заставку по короткому пути: в длинном
                    // бывают пробелы и кириллица в имени пользователя.
                    _system.WriteString(Desktop, "SCRNSAVE.EXE", ShortPath(scr));
                    _system.WriteString(Desktop, "ScreenSaveActive", "1");
                    if (string.IsNullOrEmpty(_system.ReadString(Desktop, "ScreenSaveTimeOut")))
                        _system.WriteString(Desktop, "ScreenSaveTimeOut", "600");
                    _system.Broadcast(part, "1");
                    break;
            }
            AppLogger.Write($"🐾 Персонализация: применено «{part}»");
        }

        /// <summary>Вернуть пункт как было до первого применения.</summary>
        public void Revert(PersonalizationPart part)
        {
            UndoRecord? record;
            lock (_gate)
            {
                var all = Load();
                if (!all.TryGetValue(part.ToString(), out record)) return;
            }

            foreach (var v in record.Values)
            {
                if (!v.Existed) _system.DeleteValue(v.Path, v.Name);
                else if (v.Dword is int d) _system.WriteDword(v.Path, v.Name, d);
                else _system.WriteString(v.Path, v.Name, v.Text ?? "");
            }

            if (part == PersonalizationPart.Wallpaper)
            {
                var old = record.Values.FirstOrDefault(v => v.Path == Desktop && v.Name == "Wallpaper");
                _system.Broadcast(part, old?.Text ?? "");
            }
            else if (part == PersonalizationPart.Screensaver)
            {
                var old = record.Values.FirstOrDefault(v => v.Name == "ScreenSaveActive");
                _system.Broadcast(part, old?.Text == "1" ? "1" : "0");
            }
            else if (part == PersonalizationPart.Cursors)
            {
                _system.Broadcast(part, null);
            }

            lock (_gate)
            {
                var all = Load();
                all.Remove(part.ToString());
                Save(all);
            }
            AppLogger.Write($"🐾 Персонализация: возвращено «{part}»");
        }

        /// <summary>Что пункт меняет — это и запоминается перед применением.</summary>
        internal List<ValueState> Snapshot(PersonalizationPart part)
        {
            var targets = part switch
            {
                PersonalizationPart.Cursors => CursorFiles.Keys.Select(n => (Cursors, n)).Append((Cursors, "")),
                // Путь к обоям меняет сама Windows при применении — его тоже запоминаем.
                PersonalizationPart.Wallpaper => new[] { (Desktop, "Wallpaper"), (Desktop, "WallpaperStyle"), (Desktop, "TileWallpaper") },
                PersonalizationPart.LockScreen => new[] { (LockScreenCsp, "LockScreenImagePath"), (LockScreenCsp, "LockScreenImageUrl"), (LockScreenCsp, "LockScreenImageStatus") },
                PersonalizationPart.Sounds => SoundFiles.Keys.Select(e => (SoundEvents + "\\" + e + "\\.Current", "")).Append((SoundScheme, "")),
                PersonalizationPart.Screensaver => new[] { (Desktop, "SCRNSAVE.EXE"), (Desktop, "ScreenSaveActive"), (Desktop, "ScreenSaveTimeOut") },
                _ => Enumerable.Empty<(string, string)>(),
            };

            var result = new List<ValueState>();
            foreach (var (path, name) in targets)
            {
                if (name == "LockScreenImageStatus")
                {
                    var (exists, value) = _system.ReadDword(path, name);
                    result.Add(new ValueState { Path = path, Name = name, Existed = exists, Dword = exists ? value : null });
                    continue;
                }
                var text = _system.ReadString(path, name);
                result.Add(new ValueState { Path = path, Name = name, Existed = text != null, Text = text });
            }
            return result;
        }

        private static IEnumerable<string> SourceFiles(PersonalizationPart part) => part switch
        {
            PersonalizationPart.Cursors => CursorFiles.Values.Distinct(),
            PersonalizationPart.Wallpaper => new[] { WallpaperFile },
            PersonalizationPart.LockScreen => new[] { LockScreenFile },
            PersonalizationPart.Sounds => SoundFiles.Values.Distinct(),
            PersonalizationPart.Screensaver => new[] { ScreensaverFile },
            _ => Array.Empty<string>(),
        };

        private string Copy(string file, string dir)
        {
            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, file);
            File.Copy(Path.Combine(_source, file), target, overwrite: true);
            return target;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetShortPathName(string longPath, StringBuilder shortPath, uint size);

        private static string ShortPath(string path)
        {
            var sb = new StringBuilder(520);
            var len = GetShortPathName(path, sb, (uint)sb.Capacity);
            // Короткие имена бывают выключены на диске — тогда остаётся длинный путь.
            return len > 0 && len < sb.Capacity ? sb.ToString() : path;
        }

        private Dictionary<string, UndoRecord> Load()
        {
            try
            {
                if (File.Exists(_undoPath))
                    return JsonSerializer.Deserialize<Dictionary<string, UndoRecord>>(File.ReadAllText(_undoPath)) ?? new();
            }
            catch (Exception ex)
            {
                AppLogger.Write("⚠️ Персонализация: не прочитан файл отката — " + ex.Message);
            }
            return new();
        }

        private void Save(Dictionary<string, UndoRecord> all)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_undoPath)!);
            var tmp = _undoPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _undoPath, overwrite: true);
        }
    }

    /// <summary>Настоящий реестр и вызовы Windows.</summary>
    public sealed class WindowsPersonalizationSystem : IPersonalizationSystem
    {
        private const uint SPI_SETDESKWALLPAPER = 0x0014;
        private const uint SPI_SETSCREENSAVEACTIVE = 0x0011;
        private const uint SPI_SETCURSORS = 0x0057;
        private const uint SPIF_UPDATEINIFILE = 0x01;
        private const uint SPIF_SENDCHANGE = 0x02;

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SystemParametersInfo(uint action, uint param, string? vparam, uint winIni);

        private static (RegistryKey Root, string Sub) Split(string path)
        {
            var i = path.IndexOf('\\');
            var root = path[..i] switch
            {
                "HKCU" => Registry.CurrentUser,
                "HKLM" => Registry.LocalMachine,
                _ => throw new ArgumentException("Неизвестный раздел реестра: " + path),
            };
            return (root, path[(i + 1)..]);
        }

        public string? ReadString(string path, string name)
        {
            var (root, sub) = Split(path);
            using var key = root.OpenSubKey(sub);
            return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString();
        }

        public void WriteString(string path, string name, string value)
        {
            var (root, sub) = Split(path);
            using var key = root.CreateSubKey(sub, writable: true);
            // Пути к курсорам и звукам в Windows по умолчанию — с %SystemRoot%: возвращаем их тем же типом.
            key.SetValue(name, value, value.Contains('%') ? RegistryValueKind.ExpandString : RegistryValueKind.String);
        }

        public (bool Exists, int Value) ReadDword(string path, string name)
        {
            var (root, sub) = Split(path);
            using var key = root.OpenSubKey(sub);
            return key?.GetValue(name) is int v ? (true, v) : (false, 0);
        }

        public void WriteDword(string path, string name, int value)
        {
            var (root, sub) = Split(path);
            using var key = root.CreateSubKey(sub, writable: true);
            key.SetValue(name, value, RegistryValueKind.DWord);
        }

        public void DeleteValue(string path, string name)
        {
            var (root, sub) = Split(path);
            using var key = root.OpenSubKey(sub, writable: true);
            key?.DeleteValue(name, throwOnMissingValue: false);
        }

        public void Broadcast(PersonalizationPart part, string? arg)
        {
            var ok = part switch
            {
                PersonalizationPart.Cursors => SystemParametersInfo(SPI_SETCURSORS, 0, null, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE),
                PersonalizationPart.Wallpaper => SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, arg ?? "", SPIF_UPDATEINIFILE | SPIF_SENDCHANGE),
                PersonalizationPart.Screensaver => SystemParametersInfo(SPI_SETSCREENSAVEACTIVE, arg == "1" ? 1u : 0u, null, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE),
                _ => true,
            };
            if (!ok)
                AppLogger.Write($"⚠️ Персонализация: Windows не приняла «{part}» (код {Marshal.GetLastWin32Error()})");
        }
    }
}
