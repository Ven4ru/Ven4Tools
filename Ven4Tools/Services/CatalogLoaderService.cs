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
        private readonly HttpClient _httpClient = new();

        private const string RemoteCatalogUrl =
            "https://raw.githubusercontent.com/Ven4ru/Ven4Tools/main/Catalog/master.json";

        private readonly string _localCatalogPath =
            Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "Data",
                "master.json");

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

                Console.WriteLine("Loaded ONLINE catalog");

                return Deserialize(remoteJson);
            }
            catch
            {
                if (File.Exists(_localCatalogPath))
                {
                    string localJson =
                        await File.ReadAllTextAsync(_localCatalogPath);

                    Console.WriteLine("Loaded OFFLINE catalog");

                    return Deserialize(localJson);
                }

                return new MasterCatalog();
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