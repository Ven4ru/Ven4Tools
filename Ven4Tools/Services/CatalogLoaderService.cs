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
            _httpClient.Timeout = TimeSpan.FromSeconds(15);
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "Ven4Tools");
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
                    return catalog;
                }

                return new MasterCatalog { Source = "embedded" };
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