using System.Collections.Generic;

namespace Ven4Tools.Models
{
    public enum AppCategory
    {
        Браузеры,
        Офис,
        Графика,
        Разработка,
        Мессенджеры,
        Мультимедиа,
        Системные,
        ИгровыеСервисы,
        Драйверпаки,
        Другое,
        Пользовательские
    }

    /// <summary>
    /// Общий хелпер преобразования строкового имени категории в AppCategory.
    /// Используется каталогом и пользовательскими приложениями — единая точка маппинга.
    /// </summary>
    public static class AppCategoryHelper
    {
        public static AppCategory Parse(string category) => category switch
        {
            "Браузеры"         => AppCategory.Браузеры,
            "Офис"             => AppCategory.Офис,
            "Графика"          => AppCategory.Графика,
            "Разработка"       => AppCategory.Разработка,
            "Мессенджеры"      => AppCategory.Мессенджеры,
            "Мультимедиа"      => AppCategory.Мультимедиа,
            "Системные"        => AppCategory.Системные,
            "Игровые сервисы"  => AppCategory.ИгровыеСервисы,
            "Драйверпаки"      => AppCategory.Драйверпаки,
            "Пользовательские" => AppCategory.Пользовательские,
            _                  => AppCategory.Другое
        };
    }

    public class AppInfo
    {
        public string? Sha256 { get; set; }
        public string Id { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public AppCategory Category { get; set; }
        public List<string> InstallerUrls { get; set; } = new();
        public string SilentArgs { get; set; } = "/S";
        public bool IsUserAdded { get; set; } = false;
        public long RequiredSpaceMB { get; set; } = 500;
        public string? AlternativeId { get; set; }
        public bool IsInstalled { get; set; } = false;
        
        public string? LocalInstallerPath { get; set; }

        public string ChocoId { get; set; } = string.Empty;

        public string CategoryString => Category switch
        {
            AppCategory.Браузеры        => "Браузеры",
            AppCategory.Офис            => "Офис",
            AppCategory.Графика         => "Графика",
            AppCategory.Разработка      => "Разработка",
            AppCategory.Мессенджеры     => "Мессенджеры",
            AppCategory.Мультимедиа     => "Мультимедиа",
            AppCategory.Системные       => "Системные",
            AppCategory.ИгровыеСервисы  => "Игровые сервисы",
            AppCategory.Драйверпаки     => "Драйверпаки",
            AppCategory.Другое          => "Другое",
            AppCategory.Пользовательские => "Пользовательские",
            _                           => ""
        };
    }
}