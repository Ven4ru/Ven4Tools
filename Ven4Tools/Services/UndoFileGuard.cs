using System;
using System.IO;
using System.Text.Json;
using Ven4Tools.Helpers;

namespace Ven4Tools.Services
{
    /// <summary>
    /// «Применить» отказано: файл отката раздела был повреждён и отложен в <c>.bad</c>.
    /// Сообщение — готовый текст для журнала и строки состояния.
    /// </summary>
    public sealed class UndoFileBrokenException : InvalidOperationException
    {
        public string BackupPath { get; }

        public UndoFileBrokenException(string backupPath)
            : base(UndoFileGuard.BrokenMessage(backupPath))
        {
            BackupPath = backupPath;
        }
    }

    /// <summary>
    /// Общие правила для файлов отката (taskbar_theme.json, taskbar_undo.json,
    /// personalization_undo.json, debloat_undo.json).
    ///
    /// Раньше испорченный файл читался как пустой, и следующее «Применить» снимало как
    /// «оригинал» уже значения Ven4Tools — настоящий исходный вид пропадал навсегда.
    /// Теперь испорченный файл откладывается в <c>имя.bad</c>, и пока этот файл лежит
    /// рядом, «Применить» раздела отказывает. Снимается отказ удалением <c>.bad</c> или
    /// кнопкой «Вернуть»: она честно говорит, что вернуть нечего, и переименовывает
    /// <c>.bad</c> в <c>.bad-дата</c> — содержимое остаётся для разбора.
    /// </summary>
    internal static class UndoFileGuard
    {
        public static string BadPath(string path) => path + ".bad";

        /// <summary>Путь отложенного испорченного файла, пока он не разобран; иначе null.</summary>
        public static string? Broken(string path)
        {
            var bad = BadPath(path);
            try { return File.Exists(bad) ? bad : null; }
            catch (Exception) { return null; }
        }

        public static string BrokenMessage(string backupPath) =>
            $"Файл отката повреждён и сохранён как {backupPath} — сначала проверьте его или удалите, чтобы начать заново";

        public static string NothingToRevertMessage(string keptAs) =>
            $"Вернуть нечего: файл отката был повреждён, его содержимое сохранено как {keptAs}. Новые изменения снова можно применять.";

        /// <summary>Отказ «Применить», если испорченный файл ещё не разобран.</summary>
        public static void ThrowIfBroken(string path, string section)
        {
            if (Broken(path) is not { } bad) return;
            var ex = new UndoFileBrokenException(bad);
            AppLogger.Write($"⚠️ {section}: {ex.Message}");
            throw ex;
        }

        /// <summary>
        /// «Вернуть» при испорченном файле: вернуть по нему нечего. Файл не удаляется, а
        /// переименовывается (<c>.bad</c> → <c>.bad-дата</c>), чтобы «Применить» снова работало.
        /// Возвращает сообщение для пользователя или null, если испорченного файла нет.
        /// </summary>
        public static string? Acknowledge(string path, string section)
        {
            if (Broken(path) is not { } bad) return null;
            var kept = bad + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture);
            try { File.Move(bad, kept, overwrite: false); }
            catch (Exception ex)
            {
                AppLogger.Write($"⚠️ {section}: отложенный файл отката не переименован — {ex.Message}");
                kept = bad;
            }
            var message = NothingToRevertMessage(kept);
            AppLogger.Write($"↩ {section}: {message}");
            return message;
        }

        /// <summary>
        /// Чтение файла отката. Нет файла — null. Испорчен — откладывается в <c>.bad</c>, null.
        /// Не читается по другой причине (занят, нет доступа) — <paramref name="readable"/> = false:
        /// сохранять поверх нельзя, пропали бы прежние записи.
        /// </summary>
        public static T? Load<T>(string path, string section, out bool readable) where T : class
        {
            readable = true;
            try
            {
                if (!File.Exists(path)) return null;
                return JsonSerializer.Deserialize<T>(File.ReadAllText(path));
            }
            catch (JsonException ex)
            {
                var bad = FileHelper.SetAsideBroken(path);
                AppLogger.Write($"⚠️ {section}: файл отката испорчен ({ex.Message}) и отложен как {bad ?? "— не отложен"}");
                if (bad == null) readable = false;
                return null;
            }
            catch (Exception ex)
            {
                AppLogger.Write($"⚠️ {section}: файл отката не прочитан — {ex.Message}");
                readable = false;
                return null;
            }
        }
    }
}
