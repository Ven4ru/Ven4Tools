using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Ven4Tools.Models;

namespace Ven4Tools.Services
{
    public class GamificationService
    {
        private static readonly Lazy<GamificationService> _lazy = new(() => new GamificationService());
        public static GamificationService Instance => _lazy.Value;

        private readonly string _dataPath;
        private readonly SemaphoreSlim _lock = new(1, 1);

        public event Action<int, int>? PointsChanged;
        public event Action<AchievementDefinition>? AchievementUnlocked;
        public event Action<MedalDefinition>? MedalUnlocked;

        // ── Definitions ───────────────────────────────────────────────────────────

        public static readonly List<AchievementDefinition> AllAchievements = new()
        {
            new() { Id = "first_install",    Icon = "🎯", Title = "Первый шаг",       Description = "Установить первое приложение",              BonusPoints = 5  },
            new() { Id = "collector_10",     Icon = "📦", Title = "Десятка",           Description = "Установить 10 приложений",                   BonusPoints = 20 },
            new() { Id = "collector_50",     Icon = "🏆", Title = "Полтинник",         Description = "Установить 50 приложений",                   BonusPoints = 50 },
            new() { Id = "dev_setup",        Icon = "💻", Title = "Рабочее место",     Description = "Установить VS Code и Git",                   BonusPoints = 30 },
            new() { Id = "gamer_setup",      Icon = "🎮", Title = "Игровая станция",   Description = "Установить Steam и Discord",                 BonusPoints = 30 },
            new() { Id = "communicator",     Icon = "💬", Title = "Коммуникатор",      Description = "Установить 3+ мессенджера",                  BonusPoints = 25 },
            new() { Id = "updater_5",        Icon = "🔄", Title = "Актуальность",      Description = "Обновить 5+ приложений",                     BonusPoints = 25 },
            new() { Id = "bundle_basic",     Icon = "🧩", Title = "Базовый старт",     Description = "Установить базовый бандл",                   BonusPoints = 25 },
            new() { Id = "bundle_extended",  Icon = "🚀", Title = "Полный арсенал",    Description = "Установить расширенный бандл",               BonusPoints = 40 },
            new() { Id = "activator",        Icon = "🔑", Title = "Активатор",         Description = "Активировать Windows",                       BonusPoints = 25 },
            new() { Id = "loyal_7",          Icon = "⭐", Title = "Завсегдатай",       Description = "Открыть приложение 7 дней",                  BonusPoints = 15 },
            new() { Id = "loyal_30",         Icon = "🌟", Title = "Легенда присутствия", Description = "Открыть приложение 30 дней",              BonusPoints = 30 },
            new() { Id = "explorer",         Icon = "🔍", Title = "Исследователь",     Description = "Выполнить 20 поисков в каталоге",            BonusPoints = 10 },
            new() { Id = "stylist",          Icon = "🎨", Title = "Стиляга",           Description = "Сменить цветовую схему",                     BonusPoints = 5  },
            new() { Id = "multi_source",     Icon = "🔌", Title = "Мультисорс",        Description = "Установить приложение через Choco или Scoop", BonusPoints = 15 },
        };

        public static readonly List<MedalDefinition> AllMedals = new()
        {
            new() { Id = "bronze",   Icon = "🥉", Title = "Бронза",     RequirementText = "5 установок",   RequiredInstalls = 5   },
            new() { Id = "silver",   Icon = "🥈", Title = "Серебро",    RequirementText = "15 установок",  RequiredInstalls = 15  },
            new() { Id = "gold",     Icon = "🥇", Title = "Золото",     RequirementText = "30 установок",  RequiredInstalls = 30  },
            new() { Id = "diamond",  Icon = "💎", Title = "Бриллиант",  RequirementText = "75 установок",  RequiredInstalls = 75  },
            new() { Id = "crown",    Icon = "👑", Title = "Корона",     RequirementText = "150 установок", RequiredInstalls = 150 },
        };

        private static readonly (int min, int max, string name, string icon)[] Levels =
        {
            (0,    49,   "Новичок",     "🌱"),
            (50,   149,  "Пользователь","🙂"),
            (150,  349,  "Продвинутый", "⚡"),
            (350,  699,  "Эксперт",     "🔥"),
            (700,  1199, "Мастер",      "💫"),
            (1200, int.MaxValue, "Легенда", "👑"),
        };

        private GamificationService()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _dataPath = Path.Combine(appData, "Ven4Tools", "gamification.json");
        }

        // ── Public tracking methods ───────────────────────────────────────────────

        public async Task TrackInstallAsync(string appId, string appName, string source = "winget")
        {
            await _lock.WaitAsync();
            try
            {
                var data = await LoadAsync();
                int before = data.TotalPoints;

                data.InstallCount++;
                data.TotalPoints += 10;

                if (!data.InstalledAppIds.Contains(appId))
                    data.InstalledAppIds.Add(appId);

                if (source == "choco" || source == "scoop")
                    await CheckAchievementAsync(data, "multi_source");

                await CheckInstallAchievementsAsync(data);
                CheckMedals(data);
                await SaveAsync(data);

                PointsChanged?.Invoke(data.TotalPoints, data.TotalPoints - before);
            }
            finally { _lock.Release(); }
        }

        public async Task TrackUpdateAsync()
        {
            await _lock.WaitAsync();
            try
            {
                var data = await LoadAsync();
                int before = data.TotalPoints;
                data.UpdateCount++;
                data.TotalPoints += 5;
                if (data.UpdateCount >= 5) await CheckAchievementAsync(data, "updater_5");
                await SaveAsync(data);
                PointsChanged?.Invoke(data.TotalPoints, data.TotalPoints - before);
            }
            finally { _lock.Release(); }
        }

        public async Task TrackActivationAsync()
        {
            await _lock.WaitAsync();
            try
            {
                var data = await LoadAsync();
                int before = data.TotalPoints;
                data.ActivationCount++;
                data.TotalPoints += 25;
                await CheckAchievementAsync(data, "activator");
                await SaveAsync(data);
                PointsChanged?.Invoke(data.TotalPoints, data.TotalPoints - before);
            }
            finally { _lock.Release(); }
        }

        public async Task TrackSearchAsync()
        {
            await _lock.WaitAsync();
            try
            {
                var data = await LoadAsync();
                int before = data.TotalPoints;
                data.SearchCount++;
                if (data.SearchCount <= 20) data.TotalPoints += 1;
                if (data.SearchCount >= 20) await CheckAchievementAsync(data, "explorer");
                await SaveAsync(data);
                if (data.TotalPoints != before)
                    PointsChanged?.Invoke(data.TotalPoints, data.TotalPoints - before);
            }
            finally { _lock.Release(); }
        }

        public async Task TrackDailyVisitAsync()
        {
            await _lock.WaitAsync();
            try
            {
                var data = await LoadAsync();
                string today = DateTime.Today.ToString("yyyy-MM-dd");
                if (data.LastVisitDate == today) return;

                int before = data.TotalPoints;
                data.LastVisitDate = today;
                data.DailyVisitCount++;
                data.TotalPoints += 2;

                if (data.DailyVisitCount >= 7)  await CheckAchievementAsync(data, "loyal_7");
                if (data.DailyVisitCount >= 30) await CheckAchievementAsync(data, "loyal_30");

                await SaveAsync(data);
                PointsChanged?.Invoke(data.TotalPoints, data.TotalPoints - before);
            }
            finally { _lock.Release(); }
        }

        public async Task TrackBundleAsync(string bundleType)
        {
            await _lock.WaitAsync();
            try
            {
                var data = await LoadAsync();
                int before = data.TotalPoints;
                data.TotalPoints += 50;
                if (bundleType.StartsWith("basic"))    await CheckAchievementAsync(data, "bundle_basic");
                if (bundleType.StartsWith("extended")) await CheckAchievementAsync(data, "bundle_extended");
                await SaveAsync(data);
                PointsChanged?.Invoke(data.TotalPoints, data.TotalPoints - before);
            }
            finally { _lock.Release(); }
        }

        public async Task TrackPaletteChangeAsync()
        {
            await _lock.WaitAsync();
            try
            {
                var data = await LoadAsync();
                int before = data.TotalPoints;
                await CheckAchievementAsync(data, "stylist");
                await SaveAsync(data);
                if (data.TotalPoints != before)
                    PointsChanged?.Invoke(data.TotalPoints, data.TotalPoints - before);
            }
            finally { _lock.Release(); }
        }

        // ── Queries ───────────────────────────────────────────────────────────────

        public async Task<GamificationData> GetDataAsync()
        {
            await _lock.WaitAsync();
            try { return await LoadAsync(); }
            finally { _lock.Release(); }
        }

        public LevelInfo GetLevelInfo(int points)
        {
            for (int i = 0; i < Levels.Length; i++)
            {
                var (min, max, name, icon) = Levels[i];
                if (points >= min && points <= max)
                {
                    bool isMax = i == Levels.Length - 1;
                    int nextMin = isMax ? max : Levels[i + 1].min;
                    double progress = isMax ? 1.0
                        : Math.Clamp((double)(points - min) / (nextMin - min), 0, 1);
                    return new LevelInfo
                    {
                        Name           = name,
                        Icon           = icon,
                        CurrentPoints  = points,
                        LevelMinPoints = min,
                        NextLevelPoints = isMax ? max : nextMin,
                        Progress       = progress,
                        NextLevelName  = isMax ? name : Levels[i + 1].name
                    };
                }
            }
            return new LevelInfo { Name = "Легенда", Icon = "👑", CurrentPoints = points, Progress = 1 };
        }

        // ── Internals ─────────────────────────────────────────────────────────────

        private async Task CheckInstallAchievementsAsync(GamificationData data)
        {
            if (data.InstallCount >= 1)  await CheckAchievementAsync(data, "first_install");
            if (data.InstallCount >= 10) await CheckAchievementAsync(data, "collector_10");
            if (data.InstallCount >= 50) await CheckAchievementAsync(data, "collector_50");

            var ids = data.InstalledAppIds;
            if (ids.Contains("vscode") && ids.Contains("git"))
                await CheckAchievementAsync(data, "dev_setup");
            if (ids.Contains("steam") && ids.Contains("discord"))
                await CheckAchievementAsync(data, "gamer_setup");

            var messengers = new[] { "telegram", "discord", "slack", "zoom", "microsoft-teams", "element" };
            if (ids.Count(id => messengers.Contains(id)) >= 3)
                await CheckAchievementAsync(data, "communicator");
        }

        private async Task CheckAchievementAsync(GamificationData data, string id)
        {
            if (data.UnlockedAchievements.Contains(id)) return;
            data.UnlockedAchievements.Add(id);
            var def = AllAchievements.FirstOrDefault(a => a.Id == id);
            if (def != null)
            {
                data.TotalPoints += def.BonusPoints;
                AchievementUnlocked?.Invoke(def);
            }
        }

        private void CheckMedals(GamificationData data)
        {
            foreach (var medal in AllMedals)
            {
                if (!data.UnlockedMedals.Contains(medal.Id) && data.InstallCount >= medal.RequiredInstalls)
                {
                    data.UnlockedMedals.Add(medal.Id);
                    MedalUnlocked?.Invoke(medal);
                }
            }
        }

        private async Task<GamificationData> LoadAsync()
        {
            try
            {
                if (File.Exists(_dataPath))
                {
                    var json = await File.ReadAllTextAsync(_dataPath);
                    return JsonConvert.DeserializeObject<GamificationData>(json) ?? new GamificationData();
                }
            }
            catch { }
            return new GamificationData();
        }

        private async Task SaveAsync(GamificationData data)
        {
            try
            {
                data.LastUpdated = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                Directory.CreateDirectory(Path.GetDirectoryName(_dataPath)!);
                var json = JsonConvert.SerializeObject(data, Formatting.Indented);
                await File.WriteAllTextAsync(_dataPath, json);
            }
            catch { }
        }
    }
}
