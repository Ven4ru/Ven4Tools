using System;
using System.IO;

namespace Ven4Tools.Services
{
    public static class PathHelper
    {
        private static string _appDataPath;
        
        static PathHelper()
        {
            _appDataPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Ven4Tools");
            
            if (!Directory.Exists(_appDataPath))
                Directory.CreateDirectory(_appDataPath);
        }
        
        public static string MasterCatalogPath => Path.Combine(_appDataPath, "master.json");
        public static string StatsPath => Path.Combine(_appDataPath, "stats.json");
        public static string AlternativesPath => Path.Combine(_appDataPath, "alternatives.json");
        public static string HiddenAppsPath => Path.Combine(_appDataPath, "hidden.json");
        
        public static string GetAppDataFolder() => _appDataPath;
    }
}