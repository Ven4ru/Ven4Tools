using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.Win32;

namespace Ven4Tools.Services
{
    public enum AntiCheat { Vanguard, EasyAntiCheat, BattlEye, Faceit, Esea, GameGuard, Xigncode }

    /// <summary>
    /// Какие античиты стоят в системе и не запущена ли сейчас защищённая игра. Нужно,
    /// чтобы драйвер датчиков и оверлей не помешали игре (исследование 15.10.2026:
    /// подтверждённый конфликт с драйвером PawnIO — у FACEIT; у Vanguard — единичное
    /// сообщение; про остальные данных нет).
    ///
    /// «Установлен» — по записи службы или драйвера античита: многие из них запускаются
    /// только вместе с игрой. «Запущена защищённая игра» — по процессам античита или
    /// самой игры (у Vanguard служба работает всегда, поэтому для него — процессы игр Riot).
    /// Имена служб и процессов — общеизвестные; при появлении новых игр список дополнять.
    /// </summary>
    public static class AntiCheatDetector
    {
        private static readonly (AntiCheat Kind, string[] Services)[] ServiceNames =
        {
            (AntiCheat.Vanguard, new[] { "vgk", "vgc" }),
            (AntiCheat.EasyAntiCheat, new[] { "EasyAntiCheat", "EasyAntiCheat_EOS" }),
            (AntiCheat.BattlEye, new[] { "BEService", "BEDaisy" }),
            (AntiCheat.Faceit, new[] { "FACEIT", "FACEITService" }),
            (AntiCheat.Esea, new[] { "ESEADriver2" }),
            (AntiCheat.GameGuard, new[] { "npggsvc" }),
            (AntiCheat.Xigncode, new[] { "xhunter1" }),
        };

        private static readonly (AntiCheat Kind, string[] Processes)[] ProcessNames =
        {
            (AntiCheat.Vanguard, new[] { "VALORANT-Win64-Shipping", "League of Legends" }),
            (AntiCheat.EasyAntiCheat, new[] { "EasyAntiCheat", "EasyAntiCheat_EOS", "start_protected_game" }),
            (AntiCheat.BattlEye, new[] { "BEService", "BEService_x64" }),
            (AntiCheat.Faceit, new[] { "FACEIT", "FACEITService", "faceitclient" }),
            (AntiCheat.Esea, new[] { "ESEAClient" }),
            (AntiCheat.GameGuard, new[] { "GameMon", "GameMon64" }),
            (AntiCheat.Xigncode, new[] { "xxd-0.xem", "x3" }),
        };

        public static string Name(AntiCheat kind) => kind switch
        {
            AntiCheat.Vanguard => "Riot Vanguard (Valorant, League of Legends)",
            AntiCheat.EasyAntiCheat => "Easy Anti-Cheat (Fortnite, Apex Legends, Elden Ring и др.)",
            AntiCheat.BattlEye => "BattlEye (PUBG, Rainbow Six Siege, DayZ, Escape from Tarkov и др.)",
            AntiCheat.Faceit => "FACEIT Anti-Cheat",
            AntiCheat.Esea => "ESEA",
            AntiCheat.GameGuard => "nProtect GameGuard",
            AntiCheat.Xigncode => "XIGNCODE3",
            _ => kind.ToString()
        };

        public static string ShortName(AntiCheat kind) => kind switch
        {
            AntiCheat.Vanguard => "Riot Vanguard",
            AntiCheat.EasyAntiCheat => "Easy Anti-Cheat",
            AntiCheat.Faceit => "FACEIT",
            AntiCheat.GameGuard => "GameGuard",
            AntiCheat.Xigncode => "XIGNCODE3",
            _ => kind.ToString()
        };

        /// <summary>Установленные античиты — по записям служб и драйверов.</summary>
        public static IReadOnlyList<AntiCheat> Installed()
        {
            var found = new List<AntiCheat>();
            foreach (var (kind, services) in ServiceNames)
            {
                foreach (string name in services)
                {
                    try
                    {
                        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}");
                        if (key != null) { found.Add(kind); break; }
                    }
                    catch { /* нет доступа к ветке — считаем, что нет */ }
                }
            }
            return found;
        }

        /// <summary>Запущенная сейчас защищённая игра (её античит); null — нет.</summary>
        public static AntiCheat? Active() => Match(SafeProcessNames());

        internal static AntiCheat? Match(IEnumerable<string> processNames)
        {
            var names = new HashSet<string>(processNames, StringComparer.OrdinalIgnoreCase);
            foreach (var (kind, processes) in ProcessNames)
                if (processes.Any(names.Contains)) return kind;
            return null;
        }

        private static IEnumerable<string> SafeProcessNames()
        {
            Process[] all;
            try { all = Process.GetProcesses(); }
            catch { yield break; }
            foreach (var p in all)
            {
                string? name = null;
                try { name = p.ProcessName; } catch { }
                p.Dispose();
                if (name != null) yield return name;
            }
        }

        /// <summary>Программы, которые сами держат драйвер PawnIO: выключать его под ними нельзя.</summary>
        public static IReadOnlyList<string> OtherPawnIoUsers()
        {
            string[] users = { "FanControl", "LibreHardwareMonitor", "OpenRGB", "OmenMon", "pc-monitor" };
            var running = new HashSet<string>(SafeProcessNames(), StringComparer.OrdinalIgnoreCase);
            return users.Where(running.Contains).ToList();
        }
    }
}
