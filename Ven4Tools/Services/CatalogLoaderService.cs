using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

using Ven4Tools.Models;

namespace Ven4Tools.Services
{
    public class CatalogLoaderService
    {
        public static MasterCatalog? LoadedCatalog { get; private set; }
        public static event Action<MasterCatalog>? CatalogReady;

        private readonly HttpClient _httpClient;

        private const string RemoteCatalogUrl =
            "https://raw.githubusercontent.com/Ven4ru/Ven4Tools/main/Catalog/master.json";

        private readonly string _localCatalogPath =
            Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "Data",
                "master.json");

        public CatalogLoaderService()
        {
            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(AppSettings.CatalogTimeout);
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "Ven4Tools");
        }

        public void UpdateTimeout(int seconds)
        {
            _httpClient.Timeout = TimeSpan.FromSeconds(Math.Max(3, seconds));
        }

        public async Task<MasterCatalog> LoadCatalogAsync()
        {
            try
            {
                Directory.CreateDirectory(
                    Path.GetDirectoryName(_localCatalogPath)!);

                string remoteJson =
                    await _httpClient.GetStringAsync(RemoteCatalogUrl);

                await File.WriteAllTextAsync(
                    _localCatalogPath,
                    remoteJson);

                var catalog = Deserialize(remoteJson);
                catalog.Source = "online";
                LoadedCatalog = catalog;
                CatalogReady?.Invoke(catalog);
                return catalog;
            }
            catch
            {
                if (File.Exists(_localCatalogPath))
                {
                    string localJson =
                        await File.ReadAllTextAsync(_localCatalogPath);

                    var catalog = Deserialize(localJson);
                    catalog.Source = "cache";
                    LoadedCatalog = catalog;
                    CatalogReady?.Invoke(catalog);
                    return catalog;
                }

                var empty = new MasterCatalog { Source = "embedded" };
                LoadedCatalog = empty;
                CatalogReady?.Invoke(empty);
                return empty;
            }
        }

        private MasterCatalog Deserialize(string json)
        {
            return JsonSerializer.Deserialize<MasterCatalog>(
                       json,
                       new JsonSerializerOptions
                       {
                           PropertyNameCaseInsensitive = true
                       })
                   ?? new MasterCatalog();
        }
    }
}