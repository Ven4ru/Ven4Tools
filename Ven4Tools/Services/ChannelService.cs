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

        public static bool IsPreRelease
        {
            get
            {
                try
                {
                    if (!File.Exists(ChannelPath)) return false;
                    var obj = JsonConvert.DeserializeObject<dynamic>(
                        File.ReadAllText(ChannelPath));
                    return obj?.channel?.ToString() == "prerelease";
                }
                catch (Exception ex) { AppLogger.Write($"[ChannelService] {ex.Message}"); return false; }
            }
        }

        public static string InstalledVersion
        {
            get
            {
                try
                {
                    if (!File.Exists(ChannelPath)) return "";
                    var obj = JsonConvert.DeserializeObject<dynamic>(
                        File.ReadAllText(ChannelPath));
                    return obj?.version?.ToString() ?? "";
                }
                catch (Exception ex) { AppLogger.Write($"[ChannelService] {ex.Message}"); return ""; }
            }
        }
    }
}
