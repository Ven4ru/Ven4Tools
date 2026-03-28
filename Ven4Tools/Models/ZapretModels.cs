using System;                          // ← ДОБАВИТЬ
using System.Collections.Generic;      // ← ДОБАВИТЬ для List<>
using System.Linq;                     // ← ДОБАВИТЬ для .Average()

namespace Ven4Tools.Models
{
    /// <summary>
    /// Результат проверки одного сайта
    /// </summary>
    public class SiteCheckResult
    {
        public bool IsAvailable { get; set; }
        public string? Reason { get; set; }
        public DateTime? ResponseTime { get; set; }
        public int Weight { get; set; }
        public string? ContentPreview { get; set; }
    }

    /// <summary>
    /// Результат проверки всех сайтов для стратегии
    /// </summary>
    public class SiteCheckBundle
    {
        public SiteCheckResult YouTube { get; set; } = new();
        public SiteCheckResult GoogleVideo { get; set; } = new();
        public SiteCheckResult Discord { get; set; } = new();
        public SiteCheckResult Cloudflare { get; set; } = new();
        public SiteCheckResult Reddit { get; set; } = new();
        public SiteCheckResult Google { get; set; } = new();
        
        public bool HasInternet => Google.IsAvailable;
        
        public int CalculateScore()
        {
            int score = 0;
            if (YouTube.IsAvailable) score += 30;
            if (GoogleVideo.IsAvailable) score += 30;
            if (Discord.IsAvailable) score += 25;
            if (Cloudflare.IsAvailable) score += 10;
            if (Reddit.IsAvailable) score += 5;
            
            // Штраф: YouTube без видео
            if (YouTube.IsAvailable && !GoogleVideo.IsAvailable)
                score -= 20;
            
            return Math.Max(score, 0);
        }
        
        public string CalculateVerdict()
        {
            if (!YouTube.IsAvailable && !Discord.IsAvailable)
                return "❌ Не работает";
            
            if (YouTube.IsAvailable && !GoogleVideo.IsAvailable)
                return "⚠️ YouTube без видео";
            
            if (!Discord.IsAvailable)
                return "⚠️ Discord не работает";
            
            if (YouTube.IsAvailable && GoogleVideo.IsAvailable && Discord.IsAvailable)
                return "🟢 Отлично";
            
            return "⚠️ Неопределено";
        }
        
        public override string ToString()
        {
            return $"YouTube: {(YouTube.IsAvailable ? "✅" : "❌")} | Видео: {(GoogleVideo.IsAvailable ? "✅" : "❌")} | Discord: {(Discord.IsAvailable ? "✅" : "❌")} | Оценка: {CalculateScore()} | Вердикт: {CalculateVerdict()}";
        }
    }
    
    /// <summary>
    /// Модель стратегии zapret
    /// </summary>
    public class ZapretStrategy
    {
        public string Name { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public bool IsWorking { get; set; }
        public int Score { get; set; }
        public string Verdict { get; set; } = string.Empty;
        public SiteCheckBundle? LastTestResult { get; set; }
        public DateTime LastTested { get; set; }
    }
    
    /// <summary>
    /// История стратегий для самообучения
    /// </summary>
    public class StrategyHistory
    {
        public string Name { get; set; } = string.Empty;
        public int SuccessCount { get; set; }
        public int FailCount { get; set; }
        public DateTime LastSuccess { get; set; }
        public double AvgScore { get; set; }
        public List<int> ScoreHistory { get; set; } = new();
        
        public double SuccessRate => (double)SuccessCount / Math.Max(1, SuccessCount + FailCount);
        
        public double CalculateConfidence()
        {
            double recency = (DateTime.Now - LastSuccess).TotalHours < 24 ? 1.0 : 0.5;
            return SuccessRate * 0.6 + (AvgScore / 100.0) * 0.3 + recency * 0.1;
        }
        
        public void UpdateFromResult(int score, bool success)
        {
            if (success)
            {
                SuccessCount++;
                LastSuccess = DateTime.Now;
            }
            else
            {
                FailCount++;
            }
            
            ScoreHistory.Add(score);
            if (ScoreHistory.Count > 10) ScoreHistory.RemoveAt(0);
            
            AvgScore = ScoreHistory.Count > 0 ? ScoreHistory.Average() : 0;
        }
    }
    
    /// <summary>
    /// Кэшированная стратегия
    /// </summary>
    public class CachedStrategy
    {
        public string Name { get; set; } = string.Empty;
        public DateTime TestedAt { get; set; }
        public int Score { get; set; }
        public string Verdict { get; set; } = string.Empty;
        public bool IsWorking { get; set; }
    }
}