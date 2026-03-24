using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Ven4Tools.Models;

namespace Ven4Tools.Services
{
    public class CatalogLoaderService
    {
        private readonly string _cachePath;
        private readonly string _remoteUrl;

        public CatalogLoaderService()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var ven4Folder = Path.Combine(appData, "Ven4Tools");
            if (!Directory.Exists(ven4Folder))
                Directory.CreateDirectory(ven4Folder);
            
            _cachePath = Path.Combine(ven4Folder, "catalog_cache.json");
            _remoteUrl = "https://raw.githubusercontent.com/Ven4ru/Ven4Tools/main/Catalog/master.json";
        }

public async Task<MasterCatalog> LoadCatalogAsync()
{
    var remote = await TryLoadRemoteAsync();
    if (remote != null)
    {
        remote.Source = "online";
        await SaveToCacheAsync(remote);
        return remote;
    }

    var cached = await TryLoadFromCacheAsync();
    if (cached != null)
    {
        cached.Source = "cache";
        return cached;
    }

    var embedded = LoadEmbeddedCatalog();
    embedded.Source = "embedded";
    return embedded;
}

        private async Task<MasterCatalog?> TryLoadRemoteAsync()
        {
            try
            {
                // Добавляем случайный параметр для обхода кэша
                var nocacheUrl = _remoteUrl + "?t=" + DateTime.Now.Ticks;
                
                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromSeconds(5);
                client.DefaultRequestHeaders.Add("User-Agent", "Ven4Tools");
                client.DefaultRequestHeaders.Add("Cache-Control", "no-cache, no-store, must-revalidate");
                client.DefaultRequestHeaders.Add("Pragma", "no-cache");

                var json = await client.GetStringAsync(nocacheUrl);
                return JsonConvert.DeserializeObject<MasterCatalog>(json);
            }
            catch
            {
                return null;
            }
        }

        private async Task<MasterCatalog?> TryLoadFromCacheAsync()
        {
            if (!File.Exists(_cachePath))
                return null;

            try
            {
                var json = await File.ReadAllTextAsync(_cachePath);
                return JsonConvert.DeserializeObject<MasterCatalog>(json);
            }
            catch
            {
                return null;
            }
        }

        private MasterCatalog LoadEmbeddedCatalog()
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                var resourceName = "Ven4Tools.Resources.embedded_catalog.json";
                
                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream == null) return new MasterCatalog();
                
                using var reader = new StreamReader(stream);
                var json = reader.ReadToEnd();
                
                return JsonConvert.DeserializeObject<MasterCatalog>(json) ?? new MasterCatalog();
            }
            catch
            {
                return new MasterCatalog();
            }
        }

        private async Task SaveToCacheAsync(MasterCatalog catalog)
        {
            try
            {
                var json = JsonConvert.SerializeObject(catalog, Formatting.Indented);
                await File.WriteAllTextAsync(_cachePath, json);
            }
            catch { }
        }
    }
}