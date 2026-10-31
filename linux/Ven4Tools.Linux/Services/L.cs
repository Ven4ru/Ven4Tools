namespace Ven4Tools.Linux.Services;

// Перевод интерфейса: исходная строка русская, английская берётся из словаря.
// Язык - из настроек, иначе по LANG системы.
public static class L
{
    public static bool IsEnglish { get; private set; }

    public static void Init()
    {
        var lang = UserData.Settings.Language;
        if (lang is null)
        {
            var sys = Environment.GetEnvironmentVariable("LANG") ?? "";
            lang = sys.StartsWith("ru", StringComparison.OrdinalIgnoreCase) || sys.Length == 0 ? "ru" : "en";
        }
        IsEnglish = lang == "en";
    }

    public static string T(string ru) => IsEnglish && En.TryGetValue(ru, out var en) ? en : ru;

    private static readonly Dictionary<string, string> En = new()
    {
        ["Ven4Tools для Linux"] = "Ven4Tools for Linux",
        ["Загружаю каталог…"] = "Loading catalog…",
        ["Каталог"] = "Catalog",
        ["Наборы"] = "Sets",
        ["Обновления"] = "Updates",
        ["Настройки"] = "Settings",
        ["Журнал"] = "Log",
        ["Поиск программы"] = "Search apps",
        ["Все категории"] = "All categories",
        ["Только доступные для Linux"] = "Linux-ready only",
        ["Только избранное"] = "Favorites only",
        ["Включить Flathub"] = "Enable Flathub",
        ["Установить отмеченные"] = "Install selected",
        ["Снять отметки"] = "Clear selection",
        ["Сохранить отмеченные как набор"] = "Save selected as a set",
        ["Название набора"] = "Set name",
        ["Установить"] = "Install",
        ["Удалить"] = "Remove",
        ["Подождите…"] = "Please wait…",
        ["✓ Установлено"] = "✓ Installed",
        ["В очереди"] = "Queued",
        ["Нет версии для Linux"] = "No Linux version",
        ["Есть на Flathub - нажмите «Включить Flathub»"] = "Available on Flathub - press “Enable Flathub”",
        ["Нужен Snap"] = "Snap required",
        ["сборка сообщества"] = "community build",
        ["Под Linux вместо неё: "] = "On Linux instead: ",
        ["вместо"] = "instead of",
        ["Есть на Flathub, после «Включить Flathub»: "] = "On Flathub, after “Enable Flathub”: ",
        ["Готовые наборы"] = "Ready-made sets",
        ["Ваши наборы"] = "Your sets",
        ["Отметить в каталоге"] = "Select in catalog",
        ["Установить набор"] = "Install set",
        ["Удалить набор"] = "Delete set",
        ["Проверить обновления"] = "Check for updates",
        ["Обновить всё"] = "Update all",
        ["Нажмите «Проверить обновления»."] = "Press “Check for updates”.",
        ["Обновлений нет."] = "Everything is up to date.",
        ["Язык интерфейса"] = "Interface language",
        ["Перезапустите программу, чтобы сменить язык."] = "Restart the app to change the language.",
        ["Профиль"] = "Profile",
        ["Избранное и ваши наборы. Архив совместим с Ven4Tools для Windows."] = "Favorites and your sets. The archive is compatible with Ven4Tools for Windows.",
        ["Экспорт профиля"] = "Export profile",
        ["Импорт профиля"] = "Import profile",
        ["Для дома"] = "Home",
        ["Для работы"] = "Work",
        ["Для игр"] = "Gaming",
        ["Для разработки"] = "Development",
        ["Готово."] = "Done.",
        ["Не получилось, подробности выше."] = "Failed, see details above.",
        ["Ошибка: "] = "Error: ",
        ["кэш"] = "cache",
        ["встроенный"] = "built-in",
        ["Подпись каталога не сошлась: "] = "Catalog signature mismatch: ",
        ["Источник недоступен: "] = "Source unavailable: ",
        ["Подпись встроенного каталога не сошлась"] = "Built-in catalog signature mismatch",
        ["Кэш каталога не записался: "] = "Catalog cache was not saved: ",
        ["Каталог v{0} старее принятого v{1}, источник пропущен: "] = "Catalog v{0} is older than accepted v{1}, source skipped: ",
        ["Не найден менеджер пакетов: поставьте Flatpak средствами своей системы."] = "No package manager found: install Flatpak with your system's tools.",
    };
}
