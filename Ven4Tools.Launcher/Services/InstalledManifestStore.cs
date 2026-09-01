using System;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Ven4Tools.Launcher.Models;

namespace Ven4Tools.Launcher.Services;

/// <summary>
/// Локальный кэш состава установленной версии клиента: тот же формат
/// <see cref="ClientFileManifest"/>, что и у подписанного client-manifest.json,
/// но БЕЗ подписи — это не источник доверия, а запись «вот эти файлы с этими
/// хешами мы сами положили на диск последней успешной установкой». Каждый хеш
/// в нём был проверен в момент записи файла (полная установка распаковывает
/// архив с проверенным SHA256, дельта проверяет SHA256 каждого файла отдельно).
///
/// Файл лежит НЕ внутри папки клиента, а в данных лаунчера
/// (%LOCALAPPDATA%\Ven4Tools\Launcher\installed-client-manifest.json): дельта
/// точечно перезаписывает и удаляет файлы в папке клиента, и держать там же
/// метаданные о том, что в этой папке лежит, — значит рисковать потерять их
/// ровно тем процессом, который они описывают.
///
/// Отказоустойчивость: отсутствующий, битый или нечитаемый файл — это null, а
/// не исключение. Отсутствие кэша всего лишь означает «дельта недоступна,
/// качаем полностью», и ломать из-за него обновление нельзя.
///
/// Путь передаётся через конструктор (значение по умолчанию — штатное
/// расположение), чтобы сервис оставался тестируемым и переиспользуемым
/// будущей функцией «проверить и починить установленный клиент».
/// </summary>
internal sealed class InstalledManifestStore
{
    public const string FileName = "installed-client-manifest.json";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _path;

    public InstalledManifestStore()
        : this(DefaultPath)
    {
    }

    internal InstalledManifestStore(string path)
    {
        _path = path;
    }

    /// <summary>
    /// Штатное расположение кэша: %LOCALAPPDATA%\Ven4Tools\Launcher\installed-client-manifest.json.
    /// </summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Ven4Tools", "Launcher", FileName);

    /// <summary>Путь к файлу кэша этого экземпляра.</summary>
    public string FilePath => _path;

    /// <summary>
    /// Читает кэш. Возвращает null, если файла нет, он битый или без списка файлов.
    /// Исключений наружу не бросает.
    /// </summary>
    public ClientFileManifest? Load()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            string json = File.ReadAllText(_path, Encoding.UTF8);
            var manifest = JsonSerializer.Deserialize<ClientFileManifest>(json);
            if (manifest?.Files == null || manifest.Files.Count == 0) return null;
            return manifest;
        }
        catch
        {
            // Битый/недоступный кэш равнозначен его отсутствию — дельта просто
            // окажется недоступна, а полный путь обновления работает без него.
            return null;
        }
    }

    /// <summary>
    /// Записывает кэш поверх прежнего. Возвращает false при любой ошибке записи —
    /// вызывающий код должен считать кэш отсутствующим, но не прерывать установку:
    /// файлы клиента к этому моменту уже успешно обновлены.
    /// </summary>
    public bool Save(ClientFileManifest manifest)
    {
        try
        {
            string? directory = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            // Через временный файл: прерывание записи не должно оставить наполовину
            // записанный кэш, который выглядит валидным JSON'ом лишь частично.
            string temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(manifest, WriteOptions), new UTF8Encoding(false));
            File.Move(temporary, _path, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Удаляет кэш. Вызывается, когда состав папки клиента изменился способом,
    /// после которого кэш заведомо перестал соответствовать диску, а пересчитать
    /// его не удалось. Устаревший кэш опаснее отсутствующего: по нему дельта
    /// сочла бы неизменившимися файлы, которых на диске уже нет.
    /// </summary>
    public void Invalidate()
    {
        try
        {
            if (File.Exists(_path)) File.Delete(_path);
        }
        catch
        {
            // Не критично: Load() ниже по потоку всё равно переживёт любой исход.
        }
    }
}
