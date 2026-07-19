using System;
using System.IO;
using Newtonsoft.Json;

namespace Ven4Tools.Services
{
    public static class ChannelService
    {
        private static readonly string ChannelPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ven4Tools", "channel.json");

        public static bool IsPreRelease => ReadChannelFile()?.Channel == "prerelease";

        public static string InstalledVersion => ReadChannelFile()?.Version ?? "";

        private static ChannelData? ReadChannelFile()
        {
            try
            {
                if (!File.Exists(ChannelPath)) return null;
                return JsonConvert.DeserializeObject<ChannelData>(File.ReadAllText(ChannelPath));
            }
            catch (Exception ex) { AppLogger.Write($"[ChannelService] {ex.Message}"); return null; }
        }

        private class ChannelData
        {
            public string? Channel { get; set; }
            public string? Version { get; set; }
        }
    }
}
