using System;
using System.IO;
using Newtonsoft.Json;
using Ven4Tools.Helpers;
using Ven4Tools.Models;

namespace Ven4Tools.Services
{
    public static class ProfileService
    {
        private static readonly string _path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ven4Tools", Environment.GetEnvironmentVariable("VEN4TOOLS_DESIGN_PREVIEW") == "1"
                ? "design_preview_profile.json"
                : "profile.json");

        public static UserProfile Current { get; private set; } = new();
        public static event Action? Changed;

        // Файл есть, но прочитать его не удалось: в Current — значения по умолчанию,
        // и запись стёрла бы настоящие настройки. См. SettingsFileReader.
        private static bool _loadFailed;

        static ProfileService() => Load();

        public static void Load()
        {
            try
            {
                var read = SettingsFileReader.TryRead(_path, "[ProfileService]", out string json);
                _loadFailed = read == SettingsReadResult.Unreadable;
                if (read != SettingsReadResult.Read) return;

                UserProfile? profile;
                try
                {
                    // null в файле (ручная правка, повреждение, импорт чужого архива)
                    // пропускается, и поле сохраняет значение по умолчанию: иначе
                    // "PinnedAppIds": null давал NullReferenceException в полосе пинов
                    // уже на Loaded главного окна — и так при каждом запуске, потому что
                    // сам файл никто не чинит.
                    profile = JsonConvert.DeserializeObject<UserProfile>(json,
                        new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
                }
                catch (JsonException ex)
                {
                    _loadFailed = !SettingsFileReader.SetAsideCorrupt(_path, "[ProfileService]", ex.Message);
                    return;
                }
                if (profile != null) Current = profile;
            }
            catch (Exception ex) { AppLogger.Write($"[ProfileService] {ex.Message}"); }
        }

        public static void Save()
        {
            try
            {
                if (_loadFailed)
                {
                    // Профиль при старте не прочитался. Пробуем ещё раз: если файл
                    // освободился, берём настройки с диска — теряется только текущее
                    // изменение, а не весь профиль; если нет — не пишем вовсе.
                    Load();
                    if (_loadFailed)
                    {
                        AppLogger.Write("[ProfileService] Сохранение пропущено: профиль не удалось прочитать, запись стёрла бы настройки");
                        return;
                    }
                    AppLogger.Write("[ProfileService] Профиль перечитан с диска; последнее изменение не сохранено — повторите его");
                    Changed?.Invoke();
                    return;
                }

                FileHelper.WriteAllTextAtomic(_path, JsonConvert.SerializeObject(Current, Formatting.Indented));
                Changed?.Invoke();
            }
            catch (Exception ex) { AppLogger.Write($"[ProfileService] {ex.Message}"); }
        }

        // Перечитать профиль с диска и уведомить подписчиков.
        // Используется после импорта настроек из файла.
        public static void Reload()
        {
            Load();
            Changed?.Invoke();
        }

    }
}
