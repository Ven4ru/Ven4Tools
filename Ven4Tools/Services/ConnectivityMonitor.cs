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
        // Защита от параллельного выполнения проверок (таймер + ручные вызовы)
        private static int _checking = 0;

        public static bool IsOnline { get; private set; } = true;

        // Fires on every change: true = came online, false = went offline
        public static event Action<bool>? StatusChanged;

        public static void Start()
        {
            // Защита от повторного вызова: старый таймер останавливаем и освобождаем,
            // иначе каждый Start() создавал бы новый таймер и они копились бы навсегда
            if (_timer != null)
            {
                _timer.Dispose();
                _timer = null;
            }
            _timer = new Timer(_ => _ = CheckAsync(), null, TimeSpan.Zero, TimeSpan.FromSeconds(30));
        }

        public static void Stop() => _timer?.Dispose();

        public static async Task CheckAsync()
        {
            // Пропускаем, если проверка уже идёт — иначе гонка за _lastState/IsOnline
            if (Interlocked.CompareExchange(ref _checking, 1, 0) != 0) return;
            try
            {
                bool online = await PingAsync();
                if (online == _lastState) return;
                _lastState = online;
                IsOnline   = online;
                StatusChanged?.Invoke(online);
            }
            finally
            {
                Interlocked.Exchange(ref _checking, 0);
            }
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
