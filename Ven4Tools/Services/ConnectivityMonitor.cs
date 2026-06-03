using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Ven4Tools.Services
{
    public static class ConnectivityMonitor
    {
        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        private static Timer? _timer;
        private static bool _lastState = true;

        public static bool IsOnline { get; private set; } = true;

        // Fires on every change: true = came online, false = went offline
        public static event Action<bool>? StatusChanged;

        public static void Start()
        {
            _timer = new Timer(_ => _ = CheckAsync(), null, TimeSpan.Zero, TimeSpan.FromSeconds(30));
        }

        public static void Stop() => _timer?.Dispose();

        public static async Task CheckAsync()
        {
            bool online = await PingAsync();
            if (online == _lastState) return;
            _lastState = online;
            IsOnline   = online;
            StatusChanged?.Invoke(online);
        }

        private static async Task<bool> PingAsync()
        {
            try
            {
                // Заголовки передаём через HttpRequestMessage — не трогаем DefaultRequestHeaders (не thread-safe)
                var req = new HttpRequestMessage(HttpMethod.Head, "https://one.one.one.one");
                req.Headers.Add("User-Agent", "ven4-ping");
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                return true;
            }
            catch { }

            // Fallback: try raw TCP
            try
            {
                using var tcp = new System.Net.Sockets.TcpClient();
                await tcp.ConnectAsync("8.8.8.8", 53);
                return true;
            }
            catch { return false; }
        }
    }
}
