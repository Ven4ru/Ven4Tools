using System.Collections.Generic;
using Newtonsoft.Json;

namespace Ven4Tools.Models
{
    public class GamificationData
    {
        [JsonProperty("totalPoints")]
        public int TotalPoints { get; set; }

        [JsonProperty("installCount")]
        public int InstallCount { get; set; }

        [JsonProperty("updateCount")]
        public int UpdateCount { get; set; }

        [JsonProperty("activationCount")]
        public int ActivationCount { get; set; }

        [JsonProperty("searchCount")]
        public int SearchCount { get; set; }

        [JsonProperty("dailyVisitCount")]
        public int DailyVisitCount { get; set; }

        [JsonProperty("lastVisitDate")]
        public string LastVisitDate { get; set; } = "";

        [JsonProperty("installedAppIds")]
        public List<string> InstalledAppIds { get; set; } = new();

        [JsonProperty("unlockedAchievements")]
        public List<string> UnlockedAchievements { get; set; } = new();

        [JsonProperty("unlockedMedals")]
        public List<string> UnlockedMedals { get; set; } = new();

        [JsonProperty("lastUpdated")]
        public string LastUpdated { get; set; } = "";
    }

    public class AchievementDefinition
    {
        public string Id { get; set; } = "";
        public string Icon { get; set; } = "";
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public int BonusPoints { get; set; }
    }

    public class MedalDefinition
    {
        public string Id { get; set; } = "";
        public string Icon { get; set; } = "";
        public string Title { get; set; } = "";
        public string RequirementText { get; set; } = "";
        public int RequiredInstalls { get; set; }
    }

    public class LevelInfo
    {
        public string Name { get; set; } = "";
        public string Icon { get; set; } = "";
        public int CurrentPoints { get; set; }
        public int LevelMinPoints { get; set; }
        public int NextLevelPoints { get; set; }
        public double Progress { get; set; }
        public string NextLevelName { get; set; } = "";
    }
}
