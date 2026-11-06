using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ven4Tools.Linux.Models;
using Ven4Tools.Services;

namespace Ven4Tools.Linux.Services;

// Каталоги берутся с тех же адресов, что у клиента Windows; подпись обязательна
// для каждого источника (fail-closed). Источник с версией ниже уже принятой
// отвергается (защита от отката). Без сети - кэш, затем встроенная копия.
// Основной - отдельный каталог Linux (linux-catalog.json); master.json + linux-packages.json
// остаются запасным путём, если каталог Linux не прошёл проверку ни из одного источника.
public static class CatalogLoader
{
    private static readonly string[] MasterSources =
    {
        "https://ven4tools.ru/catalog/master.json",
        "https://cdn.ven4tools.ru/master.json",
        "https://raw.githubusercontent.com/Ven4ru/Ven4Tools/main/Catalog/master.json",
    };

    private static readonly string[] LinuxSources =
    {
        "https://ven4tools.ru/catalog/linux-catalog.json",
        "https://cdn.ven4tools.ru/linux-catalog.json",
        "https://raw.githubusercontent.com/Ven4ru/Ven4Tools/main/Catalog/linux-catalog.json",
    };

    // Для проверок в ВМ: дополнительный адрес каталога Linux первым в списке. Подпись проверяется так же,
    // поэтому подменить им каталог без ключа нельзя
    private const string LinuxSourceOverrideEnv = "VEN4TOOLS_LINUX_CATALOG_URL";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(8),
        DefaultRequestHeaders = { { "User-Agent", "Ven4Tools-Linux" } }
    };

    // Хотя бы один источник ответил по сети, пусть и ошибкой: для строки «Интернет доступен»
    public static bool ServerReachable { get; private set; }

    private sealed record Spec(string File, string[] Sources, string Guard);

    private static readonly Spec Master = new("master.json", MasterSources, "catalog_guard");
    private static readonly Spec Linux = new("linux-catalog.json", LinuxSources, "linux_catalog_guard");

    // Поле kind разделяет каталоги, подписанные одним ключом
    private sealed class KindProbe
    {
        [JsonPropertyName("kind")] public string? Kind { get; set; }
        [JsonPropertyName("version")] public int Version { get; set; }
    }

    public static async Task<(MasterCatalog Catalog, string Source)> LoadAsync(Action<string> log)
    {
        var (json, source) = await LoadSignedAsync(Master, Master.Sources, k => k is null, log);
        return (JsonSerializer.Deserialize<MasterCatalog>(json) ?? new MasterCatalog(), source);
    }

    // null - каталог Linux не прошёл проверку ни из сети, ни из кэша, ни встроенный
    public static async Task<(LinuxCatalog Catalog, string Source)?> LoadLinuxAsync(Action<string> log)
    {
        var sources = Linux.Sources.ToList();
        if (Environment.GetEnvironmentVariable(LinuxSourceOverrideEnv) is { Length: > 0 } extra
            && Uri.TryCreate(extra, UriKind.Absolute, out var u) && (u.Scheme == "https" || u.Scheme == "http"))
            sources.Insert(0, extra);
        try
        {
            var (json, source) = await LoadSignedAsync(Linux, sources.ToArray(), k => k == LinuxCatalog.KindValue, log);
            var catalog = JsonSerializer.Deserialize<LinuxCatalog>(json);
            return catalog is null ? null : (catalog, source);
        }
        catch (Exception ex)
        {
            log(L.T("Каталог Linux недоступен, беру каталог Windows: ") + ex.Message);
            return null;
        }
    }

    public static LinuxPackageMap LoadPackageMap() =>
        JsonSerializer.Deserialize<LinuxPackageMap>(ReadResource("linux-packages.json"))
        ?? new LinuxPackageMap();

    // Названия программ каталога Windows - для подписей «чего под Linux нет» в наборах.
    // Встроенная копия, подпись проверяется как обычно
    public static Dictionary<string, string> LoadWindowsNames()
    {
        try
        {
            var json = ReadResource("master.json");
            if (!CatalogSignatureVerifier.Verify(json, ReadResource("master.json.sig"))) return new();
            var c = JsonSerializer.Deserialize<MasterCatalog>(json);
            return c?.Apps.GroupBy(a => a.Id).ToDictionary(g => g.Key, g => g.First().Name) ?? new();
        }
        catch { return new(); }
    }

    private static async Task<(string Json, string Source)> LoadSignedAsync(
        Spec spec, string[] sources, Func<string?, bool> kindOk, Action<string> log)
    {
        var lastSeen = ReadGuard(spec);
        foreach (var url in sources)
        {
            try
            {
                var json = await Http.GetStringAsync(url);
                var sig = await Http.GetStringAsync(url + ".sig");
                if (!CatalogSignatureVerifier.Verify(json, sig))
                {
                    log(L.T("Подпись каталога не сошлась: ") + url);
                    continue;
                }
                var probe = JsonSerializer.Deserialize<KindProbe>(json) ?? new KindProbe();
                if (!kindOk(probe.Kind))
                {
                    log(L.T("Не тот каталог по этому адресу: ") + url);
                    continue;
                }
                if (probe.Version < lastSeen)
                {
                    log(string.Format(L.T("Каталог v{0} старее принятого v{1}, источник пропущен: "), probe.Version, lastSeen) + url);
                    continue;
                }
                ServerReachable = true;
                SaveCache(spec, json, sig, probe.Version, log);
                return (json, new Uri(url).Host);
            }
            catch (Exception ex)
            {
                // Сервер ответил ошибкой (например, 404 до выкладки каталога) - связь при этом есть
                if (ex is HttpRequestException { StatusCode: not null }) ServerReachable = true;
                log(L.T("Источник недоступен: ") + $"{url} ({ex.Message})");
            }
        }

        if (TryReadCache(spec, kindOk) is { } cached)
            return (cached, L.T("кэш"));

        var embeddedJson = ReadResource(spec.File);
        var embeddedSig = ReadResource(spec.File + ".sig");
        if (!CatalogSignatureVerifier.Verify(embeddedJson, embeddedSig)
            || !kindOk((JsonSerializer.Deserialize<KindProbe>(embeddedJson) ?? new KindProbe()).Kind))
            throw new InvalidOperationException(L.T("Подпись встроенного каталога не сошлась"));
        return (embeddedJson, L.T("встроенный"));
    }

    private static string CachePath(Spec spec) => Path.Combine(AppPaths.Cache, spec.File);
    private static string GuardPath(Spec spec) => Path.Combine(AppPaths.State, spec.Guard);

    private static void SaveCache(Spec spec, string json, string sig, int version, Action<string> log)
    {
        try
        {
            // В кэш пишется ровно та пара json+sig, что прошла проверку
            AppPaths.WriteAtomic(CachePath(spec), json);
            AppPaths.WriteAtomic(CachePath(spec) + ".sig", sig);
            if (version > ReadGuard(spec))
                AppPaths.WriteAtomic(GuardPath(spec), version.ToString());
        }
        catch (Exception ex)
        {
            log(L.T("Кэш каталога не записался: ") + ex.Message);
        }
    }

    private static string? TryReadCache(Spec spec, Func<string?, bool> kindOk)
    {
        try
        {
            var path = CachePath(spec);
            if (!File.Exists(path) || !File.Exists(path + ".sig")) return null;
            var json = File.ReadAllText(path);
            // Кэш с несовпавшей подписью или чужого вида не принимается (fail-closed)
            if (!CatalogSignatureVerifier.Verify(json, File.ReadAllText(path + ".sig"))) return null;
            return kindOk((JsonSerializer.Deserialize<KindProbe>(json) ?? new KindProbe()).Kind) ? json : null;
        }
        catch { return null; }
    }

    private static int ReadGuard(Spec spec)
    {
        try
        {
            var path = GuardPath(spec);
            return File.Exists(path) && int.TryParse(File.ReadAllText(path).Trim(), out var v) ? v : 0;
        }
        catch { return 0; }
    }

    private static string ReadResource(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Нет ресурса {name}");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}
