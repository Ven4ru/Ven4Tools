using System.Collections.Generic;

namespace Ven4Tools.Models
{
    public enum AppCategory
    {
        Браузеры,
        Офис,
        Графика,
        Разработка,
        ИИ,
        Мессенджеры,
        Мультимедиа,
        Системные,
        ИгровыеСервисы,
        Драйверпаки,
        Другое,
        Пользовательские,
        // Новые категории — только в конец: пользовательские приложения хранят категорию
        // числом (UserAppsStore), и вставка в середину сдвинула бы их категории. Место в
        // списке каталога задаёт SortOrder, а не номер.
        Компоненты
    }

    /// <summary>
    /// Общий хелпер преобразования строкового имени категории в AppCategory.
    /// Используется каталогом и пользовательскими приложениями — единая точка маппинга.
    /// </summary>
    public static class AppCategoryHelper
    {
        // Порядок категорий в каталоге: «Компоненты» — сразу после «Драйверпаков».
        private static readonly AppCategory[] DisplayOrder =
        {
            AppCategory.Браузеры, AppCategory.Офис, AppCategory.Графика, AppCategory.Разработка,
            AppCategory.ИИ, AppCategory.Мессенджеры, AppCategory.Мультимедиа, AppCategory.Системные,
            AppCategory.ИгровыеСервисы, AppCategory.Драйверпаки, AppCategory.Компоненты,
            AppCategory.Другое, AppCategory.Пользовательские
        };

        public static int SortOrder(AppCategory category)
        {
            int index = System.Array.IndexOf(DisplayOrder, category);
            return index >= 0 ? index : DisplayOrder.Length;
        }

        public static AppCategory Parse(string category) => category switch
        {
            "Браузеры"         => AppCategory.Браузеры,
            "Офис"             => AppCategory.Офис,
            "Графика"          => AppCategory.Графика,
            "Разработка"       => AppCategory.Разработка,
            "ИИ"               => AppCategory.ИИ,
            "Мессенджеры"      => AppCategory.Мессенджеры,
            "Мультимедиа"      => AppCategory.Мультимедиа,
            "Системные"        => AppCategory.Системные,
            "Игровые сервисы"  => AppCategory.ИгровыеСервисы,
            "Драйверпаки"      => AppCategory.Драйверпаки,
            "Компоненты"       => AppCategory.Компоненты,
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

        public string? RegionNote { get; set; }

        public string? LocalInstallerPath { get; set; }

        public string ChocoId { get; set; } = string.Empty;

        public string CategoryString => Category switch
        {
            AppCategory.Браузеры        => "Браузеры",
            AppCategory.Офис            => "Офис",
            AppCategory.Графика         => "Графика",
            AppCategory.Разработка      => "Разработка",
            AppCategory.ИИ              => "ИИ",
            AppCategory.Мессенджеры     => "Мессенджеры",
            AppCategory.Мультимедиа     => "Мультимедиа",
            AppCategory.Системные       => "Системные",
            AppCategory.ИгровыеСервисы  => "Игровые сервисы",
            AppCategory.Драйверпаки     => "Драйверпаки",
            AppCategory.Компоненты      => "Компоненты",
            AppCategory.Другое         => "Другое",
            AppCategory.Пользовательские => "Пользовательские",
            _                           => ""
        };
    }
}