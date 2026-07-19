using System;
using System.IO;
using Newtonsoft.Json;
using Ven4Tools.Helpers;

namespace Ven4Tools.Services
{
    public static class AppSettings
    {
        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ven4Tools", "settings.json");

        public static int CatalogTimeout { get; private set; } = 10;
        public static int CheckTimeout { get; private set; } = 15;
        public static bool Notifications { get; private set; } = true;
        public static bool UpdateNotifications { get; private set; } = true;

        public static event Action? Changed;

        // Последний загруженный объект настроек. Хранится, чтобы при записи через
        // Save() сохранялись любые поля, которых нет среди четырёх известных выше
        // (иначе новое поле молча затиралось бы при каждом сохранении).
        private static SettingsData _data = new();

        static AppSettings() => Reload();

        public static void Reload()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return;
                var data = JsonConvert.DeserializeObject<SettingsData>(File.ReadAllText(SettingsPath));
                if (data == null) return;
                _data = data;
                CatalogTimeout = data.CatalogTimeout;
                CheckTimeout = data.CheckTimeout;
                Notifications = data.Notifications;
                UpdateNotifications = data.UpdateNotifications;
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "Ошибка загрузки настроек");
            }
        }

        /// <summary>
        /// Записывает настройки в тот же файл, из которого читает <see cref="Reload"/>.
        /// Единая точка сериализации <see cref="SettingsData"/> — раньше SystemTab
        /// писал файл в обход этого класса анонимным объектом из четырёх полей.
        /// Обновляет статические свойства и вызывает <see cref="Changed"/> без повторного
        /// чтения файла.
        /// </summary>
        public static void Save(int catalogTimeout, int checkTimeout, bool notifications, bool updateNotifications)
        {
            _data.CatalogTimeout      = CatalogTimeout      = catalogTimeout;
            _data.CheckTimeout        = CheckTimeout        = checkTimeout;
            _data.Notifications       = Notifications       = notifications;
            _data.UpdateNotifications = UpdateNotifications = updateNotifications;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
                FileHelper.WriteAllTextAtomic(SettingsPath, JsonConvert.SerializeObject(_data, Formatting.Indented));
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "Ошибка сохранения настроек");
            }

            Changed?.Invoke();
        }

        private sealed class SettingsData
        {
            public int CatalogTimeout { get; set; } = 10;
            public int CheckTimeout { get; set; } = 15;
            public bool Notifications { get; set; } = true;
            public bool UpdateNotifications { get; set; } = true;
        }

        public static void NotifyChanged()
        {
            Reload();
            Changed?.Invoke();
        }
    }
}
