using System;
using System.Collections.Generic;

namespace Ven4Tools.Services
{
    /// <summary>
    /// Сопоставление русского слова-категории (то, что пользователь вводит в поиск
    /// каталога) с winget-тегами манифестов. Нужно, чтобы запрос вроде «видео» или
    /// «антивирус» — который не является именем пакета и потому ничего не находит
    /// через winget search --name — превращался в поиск по тегу (SearchByTagAsync),
    /// где такие приложения размечены.
    ///
    /// Значения — только проверенные (дающие релевантную выдачу) теги реальных
    /// манифестов winget-pkgs; новые непроверенные теги сюда не добавлять.
    /// </summary>
    public static class CategorySearchMap
    {
        private static readonly Dictionary<string, string[]> _map =
            new(StringComparer.OrdinalIgnoreCase)
        {
            ["видео"]                = new[] { "video" },
            ["фото"]                 = new[] { "photo", "image" },
            ["фотография"]           = new[] { "photo", "image" },
            ["аудио"]                = new[] { "audio", "music" },
            ["музыка"]               = new[] { "audio", "music" },
            ["браузер"]              = new[] { "browser" },
            ["браузеры"]             = new[] { "browser" },
            ["разработка"]           = new[] { "development", "ide" },
            ["разработчик"]          = new[] { "development", "ide" },
            ["мессенджеры"]          = new[] { "messaging", "chat" },
            ["мессенджер"]           = new[] { "messaging", "chat" },
            ["чат"]                  = new[] { "messaging", "chat" },
            ["чаты"]                 = new[] { "messaging", "chat" },
            ["антивирус"]            = new[] { "antivirus", "security" },
            ["безопасность"]         = new[] { "antivirus", "security" },
            ["игры"]                 = new[] { "game", "gaming" },
            ["игра"]                 = new[] { "game", "gaming" },
            ["офис"]                 = new[] { "office", "productivity" },
            ["система"]              = new[] { "utility", "system-utility" },
            ["утилиты"]              = new[] { "utility", "system-utility" },
            ["утилита"]              = new[] { "utility", "system-utility" },
            ["драйверы"]             = new[] { "driver" },
            ["драйвер"]              = new[] { "driver" },
            ["торренты"]             = new[] { "torrent" },
            ["торрент"]              = new[] { "torrent" },
            ["vpn"]                  = new[] { "vpn" },
            ["впн"]                  = new[] { "vpn" },
            ["архиватор"]            = new[] { "archive", "compression" },
            ["архивы"]               = new[] { "archive", "compression" },
            ["архив"]                = new[] { "archive", "compression" },
            ["почта"]                = new[] { "email" },
            ["пароли"]               = new[] { "password-manager" },
            ["пароль"]               = new[] { "password-manager" },
            ["заметки"]              = new[] { "note-taking" },
            ["заметка"]              = new[] { "note-taking" },
            ["календарь"]            = new[] { "calendar" },
            ["облако"]               = new[] { "cloud-storage" },
            ["облачное хранилище"]   = new[] { "cloud-storage" },
            ["эмулятор"]             = new[] { "emulator" },
            ["эмуляторы"]            = new[] { "emulator" },
            ["виртуализация"]        = new[] { "virtualization" },
            ["удалённый доступ"]     = new[] { "remote-desktop" },
            ["удаленный доступ"]     = new[] { "remote-desktop" },
            ["запись экрана"]        = new[] { "screenshot", "recording" },
            ["скриншот"]             = new[] { "screenshot", "recording" },
            ["стриминг"]             = new[] { "streaming" },
            ["кодек"]                = new[] { "codec" },
            ["кодеки"]               = new[] { "codec" },
            ["pdf"]                  = new[] { "pdf" },
            ["редактор"]             = new[] { "editor", "text-editor" },
            ["текстовый редактор"]   = new[] { "editor", "text-editor" },
            ["загрузчик"]            = new[] { "download-manager" },
            ["менеджер закачек"]     = new[] { "download-manager" },
            ["лаунчер"]              = new[] { "launcher" },
            ["плеер"]                = new[] { "media-player", "player" },
            ["видеоплеер"]           = new[] { "media-player", "player" },
            ["медиаплеер"]           = new[] { "media-player", "player" },
        };

        /// <summary>
        /// Возвращает список winget-тегов для введённого пользователем слова, либо
        /// null, если слово не является известной категорией (тогда вызывающий код
        /// оставляет обычный поиск по имени). Сравнение регистронезависимое.
        /// </summary>
        public static string[]? TryGetTags(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return null;
            return _map.TryGetValue(query.Trim(), out var tags) ? tags : null;
        }
    }
}
