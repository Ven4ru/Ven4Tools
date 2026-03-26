using System;

namespace Ven4Tools.Models
{
public class AlternativeSource
{
    public string? WingetId { get; set; }
    public string? Url { get; set; }
    public DateTime LastUpdated { get; set; }
    public int SuccessCount { get; set; }
    public string? Comment { get; set; }
    
    // Новые поля для приоритетов
    public bool Priority { get; set; }        // Приоритет для winget ID
    public bool UrlPriority { get; set; }     // Приоритет для ссылки
}
}