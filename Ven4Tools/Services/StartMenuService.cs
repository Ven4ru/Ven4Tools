using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using Microsoft.Win32;

namespace Ven4Tools.Services
{
    /// <summary>Как включать, выключать и показывать программу «Свой Пуск»; вынесено ради тестов.</summary>
    public interface IStartMenuHost
    {
        /// <summary>Положить программу из папки клиента на постоянное место (прежняя копия уже закрыта).</summary>
        void Install(string source, string destination);
        /// <summary>Запустить без прав администратора; false — не вышло (поднимет автозапуск при входе).</summary>
        bool Start(string exe, string arguments);
        /// <summary>Попросить работающую копию закрыться и дождаться этого.</summary>
        void Stop();
        /// <summary>Попросить работающую копию открыть меню; false — программа не запущена.</summary>
        bool Show();
    }

    /// <summary>
    /// Блок 2 «Персонализации» — «Свой Пуск» (Ven4Start): своё меню программ в стиле Венчика.
    ///
    /// Отдельная маленькая программа под .NET Framework 4.8 (как ушки и заставка): родной Пуск
    /// и проводник не меняются, в explorer ничего не внедряется. Клиент только кладёт её на
    /// постоянное место (папка клиента меняется при обновлении), прописывает в автозапуск
    /// HKCU\…\Run и запускает без прав администратора. Перед первой записью в Run прежнее
    /// значение запоминается — «Вернуть как было» восстанавливает его побайтно.
    /// </summary>
    public sealed class StartMenuService
    {
        public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public const string RunName = "Ven4ToolsStart";
        public const string QuitEvent = @"Local\Ven4Start.Quit";
        public const string ShowEvent = @"Local\Ven4Start.Show";
        public const string Exe = "Ven4Start.exe";
        public const string WinKeyArg = "--win-key";

        private const string Section = "Свой Пуск";

        /// <summary>Состояние: включён ли «Свой Пуск» и что было в автозапуске до Ven4Tools.</summary>
        public sealed class State
        {
            public bool Enabled { get; set; }
            public bool WinKey { get; set; }
            public bool RunExisted { get; set; }
            public string? RunOld { get; set; }
        }

        public static StartMenuService Default { get; } = new(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ven4Tools", "start_menu.json"),
            new WindowsThemeRegistry(),
            new ShellStartMenuHost());

        /// <summary>Постоянное место программы (рядом с ушками).</summary>
        public static string InstallPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ven4Tools", "Personalization", Exe);

        public static string SourcePath => Path.Combine(AppContext.BaseDirectory, "Personalization", "Venchik", Exe);

        public static bool Available => File.Exists(SourcePath) || File.Exists(InstallPath);

        private readonly string _statePath;
        private readonly IThemeRegistry _reg;
        private readonly IStartMenuHost _host;
        private readonly string _installPath;
        private readonly string _sourcePath;
        private readonly object _lock = new();

        public StartMenuService(string statePath, IThemeRegistry reg, IStartMenuHost host,
            string? installPath = null, string? sourcePath = null)
        {
            _statePath = statePath;
            _reg = reg;
            _host = host;
            _installPath = installPath ?? InstallPath;
            _sourcePath = sourcePath ?? SourcePath;
        }

        public State Current { get { lock (_lock) return Load(out _); } }

        /// <summary>Испорченный файл состояния отложен и не разобран — «Включить» отказывает. null — всё в порядке.</summary>
        public string? BrokenBackup { get { lock (_lock) { Load(out _); return UndoFileGuard.Broken(_statePath); } } }

        public bool HasUndo => Current.Enabled;

        /// <summary>Строка автозапуска: путь в кавычках и, по выбору, открытие клавишей Win.</summary>
        public static string Command(string exe, bool winKey) => winKey ? $"\"{exe}\" {WinKeyArg}" : $"\"{exe}\"";

        /// <summary>
        /// Включить (или перевключить с другим режимом клавиши Win): закрыть прежнюю копию,
        /// положить программу, прописать автозапуск, запустить без прав администратора.
        /// </summary>
        /// <returns>false — программа на месте и в автозапуске, но сейчас не запустилась (появится после входа в Windows).</returns>
        public bool Enable(bool winKey)
        {
            lock (_lock) LoadForChange();   // испорченный файл — отказ до копирования и запуска
            _host.Stop();
            if (File.Exists(_sourcePath)) _host.Install(_sourcePath, _installPath);
            else if (!File.Exists(_installPath)) throw new FileNotFoundException("Программа «Свой Пуск» не найдена в папке Ven4Tools.", _sourcePath);
            lock (_lock)
            {
                var st = LoadForChange();
                if (!st.Enabled)
                {
                    var old = _reg.Read(RunKey, RunName);
                    st.RunExisted = old != null;
                    st.RunOld = old?.Data as string;
                }
                _reg.Write(RunKey, RunName, RegistryValueKind.String, Command(_installPath, winKey));
                st.Enabled = true;
                st.WinKey = winKey;
                Save(st);
            }
            bool started = _host.Start(_installPath, winKey ? WinKeyArg : "");
            if (!started) AppLogger.Write("⚠️ Свой Пуск не запустился сейчас — появится после следующего входа в Windows");
            return started;
        }

        /// <summary>Выключить: закрыть программу и вернуть автозапуск, каким он был до Ven4Tools.</summary>
        public void Disable()
        {
            _host.Stop();
            lock (_lock)
            {
                var st = Load(out bool readable);
                if (!readable || !st.Enabled) return;
                if (st.RunExisted && st.RunOld != null) _reg.Write(RunKey, RunName, RegistryValueKind.String, st.RunOld);
                else _reg.Delete(RunKey, RunName);
                st.Enabled = false; st.WinKey = false; st.RunExisted = false; st.RunOld = null;
                Save(st);
            }
        }

        /// <summary>«Вернуть как было» для общей кнопки раздела.</summary>
        /// <param name="message">Сообщение для строки состояния (например, «вернуть нечего» при испорченном файле); null — нет.</param>
        public void Revert(out string? message)
        {
            Disable();
            lock (_lock) message = UndoFileGuard.Acknowledge(_statePath, Section);
        }

        /// <summary>
        /// После обновления клиента в его папке лежит новая версия «Своего Пуска», а работает прежняя
        /// копия из профиля. Включён и файлы различаются — переложить и перезапустить. Зовётся при старте клиента.
        /// </summary>
        public void RefreshCopyIfStale()
        {
            try
            {
                var st = Current;
                if (!st.Enabled || BrokenBackup != null || !File.Exists(_sourcePath) || SameContent(_sourcePath, _installPath)) return;
                AppLogger.Write("Свой Пуск: обновляю копию программы после обновления Ven4Tools");
                _host.Stop();
                _host.Install(_sourcePath, _installPath);
                _host.Start(_installPath, st.WinKey ? WinKeyArg : "");
            }
            catch (Exception ex) { AppLogger.Write("⚠️ Свой Пуск: копия не обновилась — " + ex.Message); }
        }

        private static bool SameContent(string a, string b)
        {
            if (!File.Exists(b) || new FileInfo(a).Length != new FileInfo(b).Length) return false;
            using var sha = System.Security.Cryptography.SHA256.Create();
            byte[] ha, hb;
            using (var fa = File.OpenRead(a)) ha = sha.ComputeHash(fa);
            using (var fb = File.OpenRead(b)) hb = sha.ComputeHash(fb);
            return ha.AsSpan().SequenceEqual(hb);
        }

        /// <summary>Открыть меню у работающей копии; не запущена, но включена — запустить с --show.</summary>
        public bool ShowMenu()
        {
            if (_host.Show()) return true;
            var st = Current;
            if (!st.Enabled || !File.Exists(_installPath)) return false;
            return _host.Start(_installPath, st.WinKey ? WinKeyArg + " --show" : "--show");
        }

        private State LoadForChange()
        {
            var st = Load(out bool readable);
            UndoFileGuard.ThrowIfBroken(_statePath, Section);
            if (!readable)
                throw new IOException("Файл состояния «Своего Пуска» сейчас не читается — изменения не применены, чтобы не потерять прежнее состояние.");
            return st;
        }

        private State Load(out bool readable) => UndoFileGuard.Load<State>(_statePath, Section, out readable) ?? new();

        private void Save(State st) =>
            Helpers.FileHelper.WriteAllTextAtomic(_statePath, JsonSerializer.Serialize(st, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Настоящая работа с программой: копирование, запуск через оболочку без повышения прав, сигналы.</summary>
    internal sealed class ShellStartMenuHost : IStartMenuHost
    {
        public void Install(string source, string destination)
        {
            var dir = Path.GetDirectoryName(destination)!;
            // Клиент работает с правами администратора, а папка профиля доступна пользователю на запись:
            // подменённая на ссылку (junction) папка увела бы запись администратора в чужое место.
            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            for (var d = new DirectoryInfo(dir); d != null && d.FullName.Length > root.Length; d = d.Parent)
                if (d.Exists && d.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new IOException($"Папка {d.Name} — ссылка на другое место, «Свой Пуск» туда не копируется.");
            Directory.CreateDirectory(dir);
            File.Copy(source, destination, overwrite: true);
        }

        // Без прав администратора: программа лежит в папке, куда пишет сам пользователь, и запуск
        // из клиента с повышенными правами отдал бы ей (и подложенной рядом DLL) права администратора.
        // SW_SHOWNOACTIVATE (4): окна у программы при старте нет, только значок в трее.
        public bool Start(string exe, string arguments) => UnelevatedLauncher.TryStart(exe, arguments, show: 4);

        public void Stop()
        {
            try
            {
                if (!EventWaitHandle.TryOpenExisting(StartMenuService.QuitEvent, out var ev)) return;
                using (ev) ev.Set();
                // Ждём, пока прежняя копия закроется: иначе File.Copy поверх запущенного exe падает с «файл занят».
                var name = Path.GetFileNameWithoutExtension(StartMenuService.Exe);
                int session = Process.GetCurrentProcess().SessionId;
                for (int i = 0; i < 30; i++)
                {
                    Thread.Sleep(100);
                    bool alive = false;
                    foreach (var p in Process.GetProcessesByName(name))
                        using (p) { try { alive |= p.SessionId == session && !p.HasExited; } catch (Exception) { /* процесс уже закрылся */ } }
                    if (!alive) return;
                }
                AppLogger.Write("⚠️ Свой Пуск: прежняя копия не закрылась за 3 с");
            }
            catch (Exception ex) { AppLogger.Write("⚠️ Свой Пуск: сигнал закрытия — " + ex.Message); }
        }

        public bool Show()
        {
            try
            {
                if (!EventWaitHandle.TryOpenExisting(StartMenuService.ShowEvent, out var ev)) return false;
                using (ev) ev.Set();
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Write("⚠️ Свой Пуск: сигнал открытия меню — " + ex.Message);
                return false;
            }
        }
    }
}
