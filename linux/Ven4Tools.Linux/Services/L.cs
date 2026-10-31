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
        // Категории каталога
        ["Браузеры"] = "Browsers",
        ["Графика"] = "Graphics",
        ["Драйверпаки"] = "Drivers",
        ["Другое"] = "Other",
        ["ИИ"] = "AI",
        ["Игровые сервисы"] = "Game services",
        ["Компоненты"] = "Components",
        ["Мессенджеры"] = "Messengers",
        ["Мультимедиа"] = "Multimedia",
        ["Офис"] = "Office",
        ["Разработка"] = "Development",
        ["Системные"] = "System",
        ["Не найден менеджер пакетов: поставьте Flatpak средствами своей системы."] = "No package manager found: install Flatpak with your system's tools.",
        // Персонализация, Система, Автозагрузка
        ["Персонализация"] = "Personalization",
        ["Система"] = "System",
        ["Автозагрузка"] = "Startup",
        ["Оформление рабочего стола под Венчика. Всё возвращается как было одной кнопкой."] = "A Venchik look for your desktop. One button puts everything back.",
        ["Обои"] = "Wallpaper",
        ["Экран блокировки"] = "Lock screen",
        ["Применить"] = "Apply",
        ["Вернуть как было"] = "Restore",
        ["Этот рабочий стол пока не поддерживается."] = "This desktop is not supported yet.",
        ["Применено. Прежнее оформление сохранено."] = "Applied. Your previous look is saved.",
        ["Сейчас стоит ваше оформление."] = "Your own look is in place.",
        ["Здесь экран блокировки показывает обои рабочего стола: поставьте обои с Венчиком."] = "Here the lock screen shows the desktop wallpaper: apply the Venchik wallpaper.",
        ["Ядро"] = "Kernel",
        ["Рабочий стол"] = "Desktop",
        ["Процессор"] = "CPU",
        ["Память"] = "Memory",
        ["свободно"] = "free",
        ["Работает"] = "Uptime",
        ["д"] = "d",
        ["ч"] = "h",
        ["мин"] = "min",
        ["Диск"] = "Disk",
        ["потоков"] = "threads",
        ["Место на диске"] = "Disk space",
        ["Проверить"] = "Scan",
        ["Очистить отмеченное"] = "Clean selected",
        ["Корзина"] = "Trash",
        ["Удалённые файлы, которые ещё лежат в корзине"] = "Deleted files still sitting in the trash",
        ["Эскизы картинок"] = "Thumbnails",
        ["Миниатюры файлов, создаются заново при просмотре папок"] = "File previews, rebuilt when you open folders",
        ["Ненужные среды Flatpak"] = "Unused Flatpak runtimes",
        ["Среды выполнения, которые больше не нужны ни одной программе"] = "Runtimes no installed app needs anymore",
        ["Кэш пакетов системы"] = "System package cache",
        ["Скачанные установщики уже установленных обновлений"] = "Downloaded packages of updates already installed",
        ["Журнал системы"] = "System journal",
        ["Записи старше последних 100 МБ"] = "Entries beyond the latest 100 MB",
        ["Программы, которые запускаются при входе в систему. Выключенная программа остаётся установленной."] = "Apps that start when you log in. A disabled app stays installed.",
        ["Сейчас при входе не запускается ни одна программа, кроме служб самой системы."] = "No apps start at login right now, only the system's own services.",
        ["запуск при входе включён"] = "starts at login",
        ["запуск при входе выключен"] = "no longer starts at login",
    };
}
