using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Threading.Tasks;

namespace Ven4Tools.Services
{
    public class PingResult
    {
        public string Host { get; set; } = "";
        public bool Reachable { get; set; }
        public long Ms { get; set; }
        public string Display => Reachable ? $"{Ms} мс" : "недоступен";
    }

    public class ServiceCheckResult
    {
        public string Name { get; set; } = "";
        public bool Available { get; set; }
        public int Ms { get; set; }
    }

    public class AdapterInfo
    {
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
        public string Ip { get; set; } = "";
        public bool IsUp { get; set; }
    }

    public static class DiagnosticsService
    {
        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };

        // ICMP-пинг одного хоста
        public static async Task<PingResult> PingHostAsync(string host)
        {
            var result = new PingResult { Host = host };
            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(host, 3000);
                result.Reachable = reply.Status == IPStatus.Success;
                result.Ms = reply.RoundtripTime;
            }
            catch { result.Reachable = false; }
            return result;
        }

        // HTTP-проверка доступности URL
        public static async Task<ServiceCheckResult> CheckServiceAsync(string name, string url)
        {
            var result = new ServiceCheckResult { Name = name };
            var sw = Stopwatch.StartNew();
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Head, url);
                req.Headers.Add("User-Agent", "ven4-diag");
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                sw.Stop();
                result.Available = (int)resp.StatusCode < 500;
                result.Ms = (int)sw.ElapsedMilliseconds;
            }
            catch { sw.Stop(); result.Available = false; result.Ms = (int)sw.ElapsedMilliseconds; }
            return result;
        }

        // Публичный IP (два fallback)
        public static async Task<string> GetPublicIpAsync()
        {
            foreach (var url in new[] { "https://api.ipify.org", "https://checkip.amazonaws.com" })
            {
                try
                {
                    var ip = (await _http.GetStringAsync(url)).Trim();
                    if (!string.IsNullOrEmpty(ip)) return ip;
                }
                catch { }
            }
            return "не определён";
        }

        // Активные сетевые адаптеры с IP
        public static List<AdapterInfo> GetAdapters()
        {
            var list = new List<AdapterInfo>();
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus == OperationalStatus.Up &&
                        nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    {
                        var props = nic.GetIPProperties();
                        var ipv4 = props.UnicastAddresses
                            .FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                        list.Add(new AdapterInfo
                        {
                            Name  = nic.Name,
                            Type  = nic.NetworkInterfaceType.ToString(),
                            Ip    = ipv4?.Address.ToString() ?? "—",
                            IsUp  = true
                        });
                    }
                }
            }
            catch { }
            return list;
        }

        // DNS-резолюция через System.Net.Dns — без внешних процессов и проблем с кодировкой
        public static async Task<string> CheckDnsAsync(string host = "google.com")
        {
            try
            {
                var entry = await System.Net.Dns.GetHostEntryAsync(host);
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"Хост:  {entry.HostName}");
                foreach (var addr in entry.AddressList)
                    sb.AppendLine($"Адрес: {addr}");
                return sb.ToString().TrimEnd();
            }
            catch (Exception ex) { return $"Ошибка: {ex.Message}"; }
        }
    }
}
