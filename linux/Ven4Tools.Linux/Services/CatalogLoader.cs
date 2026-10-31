using System.Reflection;
using System.Text.Json;
using Ven4Tools.Linux.Models;
using Ven4Tools.Services;

namespace Ven4Tools.Linux.Services;

// Каталог берётся с тех же адресов, что у клиента Windows; подпись обязательна
// для каждого источника (fail-closed). Источник с версией ниже уже принятой
// отвергается (защита от отката). Без сети - кэш, затем встроенная копия.
public static class CatalogLoader
{
    private static readonly string[] Sources =
    {
        "https://ven4tools.ru/catalog/master.json",
        "https://cdn.ven4tools.ru/master.json",
        "https://raw.githubusercontent.com/Ven4ru/Ven4Tools/main/Catalog/master.json",
    };

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(8),
        DefaultRequestHeaders = { { "User-Agent", "Ven4Tools-Linux" } }
    };

    private static string CachePath => Path.Combine(AppPaths.Cache, "master.json");
    private static string GuardPath => Path.Combine(AppPaths.State, "catalog_guard");

    public static async Task<(MasterCatalog Catalog, string Source)> LoadAsync(Action<string> log)
    {
        var lastSeen = ReadGuard();
        foreach (var url in Sources)
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
                var catalog = Parse(json);
                if (catalog.Version < lastSeen)
                {
                    log(string.Format(L.T("Каталог v{0} старее принятого v{1}, источник пропущен: "), catalog.Version, lastSeen) + url);
                    continue;
                }
                SaveCache(json, sig, catalog.Version, log);
                return (catalog, new Uri(url).Host);
            }
            catch (Exception ex)
            {
                log(L.T("Источник недоступен: ") + $"{url} ({ex.Message})");
            }
        }

        if (TryReadCache() is { } cached)
            return (cached, L.T("кэш"));

        var embeddedJson = ReadResource("master.json");
        var embeddedSig = ReadResource("master.json.sig");
        if (!CatalogSignatureVerifier.Verify(embeddedJson, embeddedSig))
            throw new InvalidOperationException(L.T("Подпись встроенного каталога не сошлась"));
        return (Parse(embeddedJson), L.T("встроенный"));
    }

    public static LinuxPackageMap LoadPackageMap() =>
        JsonSerializer.Deserialize<LinuxPackageMap>(ReadResource("linux-packages.json"))
        ?? new LinuxPackageMap();

    private static void SaveCache(string json, string sig, int version, Action<string> log)
    {
        try
        {
            // В кэш пишется ровно та пара json+sig, что прошла проверку
            AppPaths.WriteAtomic(CachePath, json);
            AppPaths.WriteAtomic(CachePath + ".sig", sig);
            if (version > ReadGuard())
                AppPaths.WriteAtomic(GuardPath, version.ToString());
        }
        catch (Exception ex)
        {
            log(L.T("Кэш каталога не записался: ") + ex.Message);
        }
    }

    private static MasterCatalog? TryReadCache()
    {
        try
        {
            if (!File.Exists(CachePath) || !File.Exists(CachePath + ".sig")) return null;
            var json = File.ReadAllText(CachePath);
            // Кэш с несовпавшей подписью не принимается (fail-closed)
            return CatalogSignatureVerifier.Verify(json, File.ReadAllText(CachePath + ".sig")) ? Parse(json) : null;
        }
        catch { return null; }
    }

    private static int ReadGuard()
    {
        try { return File.Exists(GuardPath) && int.TryParse(File.ReadAllText(GuardPath).Trim(), out var v) ? v : 0; }
        catch { return 0; }
    }

    private static MasterCatalog Parse(string json) =>
        JsonSerializer.Deserialize<MasterCatalog>(json) ?? new MasterCatalog();

    private static string ReadResource(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Нет ресурса {name}");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}
