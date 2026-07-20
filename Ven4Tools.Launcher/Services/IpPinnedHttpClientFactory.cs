// Services/IpPinnedHttpClientFactory.cs
using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace Ven4Tools.Launcher.Services
{
    /// <summary>
    /// Создаёт HttpClient, который подключается к CDN по заранее известному IP,
    /// минуя DNS-резолвинг — на случай точечной блокировки домена cdn.ven4tools.ru
    /// по DNS (отдельно от IP).
    ///
    /// ВАЖНО (security): подменяется ТОЛЬКО точка TCP-подключения (ConnectCallback).
    /// TLS (SNI + проверка сертификата) остаётся полностью штатным — SocketsHttpHandler
    /// берёт hostname для SNI/валидации из исходного URI запроса (cdn.ven4tools.ru),
    /// а не из адреса, куда физически подключился сокет. Если IP неверный или
    /// сертификат не совпадёт — TLS-хендшейк падает как обычно (fail-closed). Никакого
    /// ServerCertificateCustomValidationCallback/skip-verify здесь нет и быть не должно.
    /// </summary>
    internal static class IpPinnedHttpClientFactory
    {
        /// <summary>
        /// Резервный IP CDN, если version.json ещё не получен ни разу (курица и яйцо:
        /// сам version.json лежит на этом же домене). Обновлять вручную при миграции
        /// VPS CDN — свериться с cdn_ip из version.json, который является источником
        /// истины после первого успешного обращения (см. CdnService.LastKnownCdnIp).
        /// </summary>
        internal const string FallbackCdnIp = "138.16.152.133";

        // Кэш клиентов по паре (IP, таймаут): один клиент на ключ живёт весь процесс,
        // как static HttpClient в остальных сервисах — пересоздание на каждый вызов
        // исчерпывало бы сокеты. Таймаут входит в ключ, т.к. разные вызывающие
        // используют разные лимиты (быстрая проверка version.json vs долгая загрузка).
        private static readonly ConcurrentDictionary<string, HttpClient> _cache = new();

        /// <summary>
        /// Кэшированный IP-pinned клиент для указанного IP и таймаута.
        /// </summary>
        public static HttpClient GetOrCreate(string pinnedIp, TimeSpan timeout)
        {
            string key = $"{pinnedIp}|{timeout.Ticks}";
            return _cache.GetOrAdd(key, _ => Create(pinnedIp, timeout));
        }

        /// <summary>
        /// Создаёт новый (некэшированный) IP-pinned клиент. Обычно используйте
        /// GetOrCreate, чтобы не плодить сокеты.
        /// </summary>
        public static HttpClient Create(string pinnedIp, TimeSpan timeout)
        {
            var handler = new SocketsHttpHandler
            {
                ConnectCallback = async (context, cancellationToken) =>
                {
                    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try
                    {
                        // Подключаемся к закреплённому IP на порт из URI (обычно 443),
                        // а не к результату DNS-резолвинга context.DnsEndPoint.Host.
                        await socket.ConnectAsync(
                            IPAddress.Parse(pinnedIp),
                            context.DnsEndPoint.Port,
                            cancellationToken);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                }
            };

            var client = new HttpClient(handler) { Timeout = timeout };
            client.DefaultRequestHeaders.Add("User-Agent", "Ven4Tools.Launcher");
            return client;
        }
    }
}
