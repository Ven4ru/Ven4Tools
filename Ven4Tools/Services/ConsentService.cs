using System;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Ven4Tools.Models;

namespace Ven4Tools.Services
{
    public class ConsentService
    {
        private readonly string _consentPath;
        
        public ConsentService()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var ven4Folder = Path.Combine(appData, "Ven4Tools");
            if (!Directory.Exists(ven4Folder))
                Directory.CreateDirectory(ven4Folder);
            
            _consentPath = Path.Combine(ven4Folder, "consent.json");
        }
        
        public async Task<Consent?> GetConsentAsync()
        {
            if (!File.Exists(_consentPath))
                return null;
                
            try
            {
                var json = await File.ReadAllTextAsync(_consentPath);
                return JsonConvert.DeserializeObject<Consent>(json);
            }
            catch
            {
                return null;
            }
        }
        
        public async Task SaveConsentAsync(bool allowStats)
        {
            var consent = new Consent
            {
                AllowStats = allowStats,
                AskedAt = DateTime.Now,
                Version = "1.0"
            };
            
            var json = JsonConvert.SerializeObject(consent, Formatting.Indented);
            await File.WriteAllTextAsync(_consentPath, json);
        }
        
        public async Task<bool> ShouldAskForConsentAsync()
        {
            var consent = await GetConsentAsync();
            return consent == null;
        }
        
        public async Task<bool> IsStatsAllowedAsync()
        {
            var consent = await GetConsentAsync();
            return consent?.AllowStats ?? false;
        }
    }
}