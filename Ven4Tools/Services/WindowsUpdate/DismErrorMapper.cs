using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Ven4Tools.Services.WindowsUpdate
{
    /// <summary>
    /// Коды DISM (CBS) — понятной причиной по-русски. DISM возвращает HRESULT кодом
    /// выхода и дублирует его в выводе строкой «Error: 0x800f081f».
    /// </summary>
    public static class DismErrorMapper
    {
        /// <summary>Успешно, но для завершения нужна перезагрузка (ERROR_SUCCESS_REBOOT_REQUIRED).</summary>
        public const int RebootRequired = 3010;

        private static readonly Regex ErrorLine = new(
            @"(?:Error|Ошибка)\s*:\s*(0x[0-9a-fA-F]{1,8}|\d+)",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Dictionary<uint, string> Known = new()
        {
            { 0x800F081F, "Windows не нашёл файлы компонента: Центр обновления их не отдал, а установочного носителя не указано. Укажите установочную флешку или ISO той же версии Windows." },
            { 0x800F0906, "Не удалось скачать файлы компонента через Центр обновления: нет интернета или доступ к серверам Microsoft закрыт. Подключитесь к сети или укажите установочную флешку/ISO." },
            { 0x800F0907, "Скачивать компоненты из Центра обновления запрещает групповая политика («Параметры установки необязательных компонентов и восстановления компонентов»). Укажите установочную флешку/ISO или попросите администратора разрешить загрузку." },
            { 0x800F0950, "Windows не смог получить файлы компонента — чаще всего из-за настроек сервера обновлений организации (WSUS) или установленного языкового пакета. Укажите установочную флешку/ISO той же версии Windows." },
            { 0x800F0954, "Компьютер настроен получать обновления с сервера организации (WSUS), и там нет файлов компонента. Укажите установочную флешку/ISO." },
            { 0x800F0922, "Обработка компонента завершилась ошибкой: нет связи с серверами Microsoft или повреждено хранилище компонентов. Попробуйте «Проверить системные файлы» и повторите." },
            { 0x800F0831, "Повреждено хранилище компонентов Windows (нет нужного манифеста). Сначала выполните «Проверить системные файлы» в разделе «Ремонт»." },
            { 0x80073701, "Повреждено хранилище компонентов Windows (не хватает сборки). Сначала выполните «Проверить системные файлы» в разделе «Ремонт»." },
            { 0x800F080C, "Компонент с таким именем в этой версии Windows не найден." },
            { 0x80070005, "Отказано в доступе — операция требует прав администратора." },
            { 0x800704C7, "Операция прервана." },
            { 0x80070020, "Файлы заняты другим процессом — возможно, сейчас идёт установка обновлений. Дождитесь её окончания и повторите." },
            { 0x8024402C, "Нет соединения с интернетом." },
            { 0x80072EFD, "Нет соединения с серверами Microsoft." },
            { 0x80072EE2, "Истекло время ожидания ответа серверов Microsoft." },
            { 0x00000057, "Неверный параметр команды DISM." },
            { 0x000002E4, "Нужны права администратора." },
        };

        /// <summary>Коды, при которых имеет смысл предложить установочную флешку/ISO.</summary>
        private static readonly HashSet<uint> SourceRelated = new()
        {
            0x800F081F, 0x800F0906, 0x800F0907, 0x800F0950, 0x800F0954, 0x800F0922
        };

        /// <summary>
        /// Код ошибки: из строки «Error: …» вывода, иначе — код выхода. 0 и 3010 — не ошибки.
        /// </summary>
        public static int ExtractErrorCode(int exitCode, string output)
        {
            var match = ErrorLine.Match(output ?? "");
            if (match.Success)
            {
                string raw = match.Groups[1].Value;
                if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    && uint.TryParse(raw.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hex))
                    return unchecked((int)hex);
                if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int dec))
                    return dec;
            }
            return exitCode;
        }

        public static bool IsSuccess(int code) => code == 0 || code == RebootRequired;

        public static bool SuggestsSourceMedia(int code) => SourceRelated.Contains(unchecked((uint)code));

        public static string Describe(int code)
        {
            uint key = unchecked((uint)code);
            return Known.TryGetValue(key, out var text)
                ? $"{text} (код 0x{key:X8})"
                : $"DISM завершился с ошибкой 0x{key:X8}. Подробности — в журнале C:\\Windows\\Logs\\DISM\\dism.log.";
        }
    }
}
