using System;
using System.IO;
using System.Reflection;
using System.Threading;
using Newtonsoft.Json;

namespace Ven4Tools.Services
{
    public sealed class HeartbeatService : IDisposable
    {
        public static readonly string HeartbeatPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ven4Tools", "heartbeat.json");

        private readonly Timer _timer;
        private readonly int   _pid     = Environment.ProcessId;
        private readonly string _version =
            Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";

        public HeartbeatService()
        {
            Beat();
            _timer = new Timer(_ => Beat(), null,
                TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
        }

        private void Beat()
        {
            try
            {
                var payload = new
                {
                    Pid       = _pid,
                    Version   = _version,
                    Timestamp = DateTime.UtcNow.ToString("O")
                };
                Directory.CreateDirectory(Path.GetDirectoryName(HeartbeatPath)!);
                File.WriteAllText(HeartbeatPath,
                    JsonConvert.SerializeObject(payload, Formatting.Indented));
            }
            catch { }
        }

        public void Dispose()
        {
            _timer.Dispose();
            try { File.Delete(HeartbeatPath); } catch { }
        }
    }
}
