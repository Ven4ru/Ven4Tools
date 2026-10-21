using System;
using Microsoft.Win32;

namespace Ven4Tools.Launcher.Services
{
    /// <summary>
    /// Итог тихого самообновления, который установщик оставляет в
    /// HKCU\Software\Ven4Tools, значение LauncherUpdateResult («код;версия», см.
    /// installer\Ven4Tools.Setup.nsi). Раньше коды возврата установщика не читал никто:
    /// при неудаче он молча возвращал прежнюю версию, лаунчер стартовал как ни в чём не
    /// бывало, и кнопка «Обновить» снова звала повторить то же самое.
    /// </summary>
    internal readonly record struct LauncherUpdateResult(int Code, string Version)
    {
        private const string KeyPath = @"Software\Ven4Tools";
        private const string ValueName = "LauncherUpdateResult";

        public bool Succeeded => Code == 0;

        /// <summary>
        /// Разбор значения «код;версия». null — значение пустое или испорчено.
        /// Чистая функция — покрыта unit-тестами.
        /// </summary>
        internal static LauncherUpdateResult? Parse(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string[] parts = raw.Split(';', 2);
            if (parts.Length != 2 || !int.TryParse(parts[0].Trim(), out int code)) return null;
            string version = parts[1].Trim();
            if (version.Length == 0 || version.Length > 32) return null;
            return new LauncherUpdateResult(code, version);
        }

        /// <summary>Причина неудачи для журнала и сообщения пользователю.</summary>
        public string DescribeFailure() => Code switch
        {
            5 => "файл лаунчера был занят (лаунчер не закрылся или файл проверял антивирус)",
            4 => "версия скачанного файла не совпала с ожидаемой",
            3 => "новый файл не удалось записать в папку лаунчера",
            _ => $"установщик завершился с кодом {Code}",
        };

        /// <summary>
        /// Читает итог и сразу стирает его: сообщить об обновлении нужно один раз.
        /// null — обновления с прошлого запуска не было.
        /// </summary>
        public static LauncherUpdateResult? ReadAndClear()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
                if (key?.GetValue(ValueName) is not string raw) return null;
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                return Parse(raw);
            }
            catch
            {
                // Нет доступа к разделу — сообщать нечего, работе лаунчера это не мешает.
                return null;
            }
        }
    }
}
