using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Ven4Tools.Services;

namespace Ven4Tools.Helpers;

/// <summary>
/// Чтение локального JSON-списка (пресеты, история установок) для сценария
/// «прочитать → изменить → атомарно записать».
///
/// Раньше любая ошибка чтения превращалась в пустой список, а следующая запись
/// сохраняла его поверх файла: файл на мгновение занят антивирусом или облачной
/// синхронизацией — и все пресеты/вся история молча стёрты одним сохранением.
/// Теперь два случая различаются:
///   * файл не читается (занят, нет доступа) — возвращаем null: вызывающий НЕ
///     должен писать, иначе затрёт данные, которые просто не смог прочитать;
///   * файл читается, но это не JSON (повреждён) — откладываем его рядом с
///     суффиксом .corrupt-ГГГГММДДччммсс и начинаем с пустого списка: данные не
///     теряются безвозвратно, а пользователь не застревает навсегда без записи.
/// </summary>
internal static class JsonListFile
{
    public static List<T>? TryLoad<T>(string path, string logPrefix)
    {
        string json;
        try
        {
            if (!File.Exists(path)) return new List<T>();
            json = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            AppLogger.Write($"{logPrefix} Файл не прочитан, изменение отменено, чтобы не затереть данные: {ex.Message}");
            return null;
        }

        try
        {
            return JsonConvert.DeserializeObject<List<T>>(json) ?? new List<T>();
        }
        catch (JsonException ex)
        {
            string backup = $"{path}.corrupt-{DateTime.Now:yyyyMMddHHmmss}";
            try
            {
                // Elevated-процесс переименовывает файл в пользовательском дереве —
                // тот же guard от подмены reparse point'ом, что и у записи.
                if (PathHelper.IsReparsePoint(Path.GetDirectoryName(path)!) || PathHelper.IsReparsePoint(path))
                    return null;
                File.Move(path, backup);
                AppLogger.Write($"{logPrefix} Файл повреждён ({ex.Message}) — отложен в {Path.GetFileName(backup)}, начинаю с пустого списка");
                return new List<T>();
            }
            catch (Exception moveEx)
            {
                AppLogger.Write($"{logPrefix} Файл повреждён и не отложен ({moveEx.Message}) — изменение отменено");
                return null;
            }
        }
    }
}
