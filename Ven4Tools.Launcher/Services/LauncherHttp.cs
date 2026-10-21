using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Ven4Tools.Launcher.Services
{
    /// <summary>
    /// Единая точка создания HttpClient лаунчера (кроме «CDN по прямому IP» — см.
    /// <see cref="IpPinnedHttpClientFactory"/>, он ходит строго напрямую).
    ///
    /// Раньше каждый сервис делал «new HttpClient» сам, и все они брали системный прокси
    /// БЕЗ учётных данных Windows: корпоративный прокси с авторизацией отвечал 407, и
    /// лаунчер не мог ни скачать клиент, ни обновиться. Теперь:
    ///   * прокси — <see cref="LauncherProxy.Current"/> («Системный» с учётными данными
    ///     Windows или «Вручную»);
    ///   * при ошибке САМОГО прокси (не подключиться к нему, отказ CONNECT, 407) запрос
    ///     без тела повторяется напрямую — неверный или умерший прокси не должен
    ///     отрезать лаунчер от обновлений. Обычные сетевые ошибки до сервера НЕ
    ///     повторяются: таймауты и детектор зависания загрузок — забота вызывающего кода
    ///     (FallbackDownloader), лишний повтор здесь удвоил бы их ожидание.
    ///
    /// Timeout клиента задаёт вызывающий — как и раньше. Range-запросы докачки идут как
    /// есть (заголовки запроса здесь не трогаются).
    /// </summary>
    internal static class LauncherHttp
    {
        // Подключение к прокси или серверу дольше этого — уже отказ. Без него мёртвый
        // прокси держал бы запрос до общего таймаута клиента (у загрузок — бесконечного),
        // и повтор напрямую так и не начался бы.
        internal static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

        // После того как прокси подвёл, а прямой запрос прошёл, какое-то время ходим
        // сразу напрямую: иначе каждый запрос заново ждал бы отказа прокси.
        private static readonly TimeSpan DirectAfterProxyFailure = TimeSpan.FromMinutes(10);
        private static long _directUntilTicks;

        /// <summary>
        /// HttpClient с прокси лаунчера и повтором напрямую при ошибке прокси.
        /// Создавайте один раз на сервис (static), как и прежде.
        /// </summary>
        public static HttpClient CreateClient(TimeSpan timeout, string userAgent = "Ven4Tools.Launcher")
        {
            var client = new HttpClient(new ProxyFallbackHandler(CreateProxiedHandler(), CreateDirectHandler()))
            {
                Timeout = timeout
            };
            client.DefaultRequestHeaders.Add("User-Agent", userAgent);
            return client;
        }

        /// <summary>Обработчик через прокси лаунчера (системный или ручной).</summary>
        internal static SocketsHttpHandler CreateProxiedHandler() => new()
        {
            UseProxy = true,
            Proxy = LauncherProxy.Current,
            ConnectTimeout = ConnectTimeout,
        };

        /// <summary>Обработчик строго напрямую, мимо любого прокси.</summary>
        internal static SocketsHttpHandler CreateDirectHandler() => new()
        {
            UseProxy = false,
            ConnectTimeout = ConnectTimeout,
        };

        /// <summary>
        /// Ошибка именно прокси, а не сервера назначения: не подключились к прокси
        /// (ConnectionError/NameResolutionError — при запросе через прокси это адрес
        /// прокси), прокси отказал в туннеле CONNECT (ProxyTunnelError).
        /// Чистая функция без сети — покрыта unit-тестами.
        /// </summary>
        internal static bool IsProxyFailure(Exception ex, bool viaProxy)
        {
            if (!viaProxy) return false;
            for (Exception? cur = ex; cur != null; cur = cur.InnerException)
            {
                if (cur is HttpRequestException hre)
                {
                    switch (hre.HttpRequestError)
                    {
                        case HttpRequestError.ProxyTunnelError:
                        case HttpRequestError.ConnectionError:
                        case HttpRequestError.NameResolutionError:
                            return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Проверка ручного прокси перед сохранением: запрос к version.json CDN (при
        /// отказе — к GitHub) СТРОГО через указанный прокси, без повтора напрямую.
        /// Возвращает null при успехе или понятную причину отказа.
        /// </summary>
        public static async Task<string?> TestProxyAsync(Uri address, string? userName, string? password, CancellationToken ct)
        {
            var proxy = LauncherProxy.CreateWebProxy(address, userName, password);
            // Локальные адреса тоже через прокси: проверяем именно его.
            proxy.BypassProxyOnLocal = false;
            using var handler = new SocketsHttpHandler { UseProxy = true, Proxy = proxy, ConnectTimeout = ConnectTimeout };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
            client.DefaultRequestHeaders.Add("User-Agent", "Ven4Tools.Launcher");

            string? lastError = null;
            foreach (var url in new[] { "https://cdn.ven4tools.ru/version.json", "https://github.com/Ven4ru/Ven4Tools/releases" })
            {
                try
                {
                    using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                    if (response.StatusCode == HttpStatusCode.ProxyAuthenticationRequired)
                        return string.IsNullOrEmpty(userName)
                            ? "Прокси требует логин и пароль (ответ 407)."
                            : "Прокси не принял логин или пароль (ответ 407).";
                    if ((int)response.StatusCode < 500)
                        return null; // ответ сервера получен через прокси — прокси работает
                    lastError = $"сервер ответил {(int)response.StatusCode}";
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    lastError = "нет ответа за 20 секунд";
                }
                catch (HttpRequestException ex) when (ex.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError)
                {
                    return $"Не удалось подключиться к прокси {address.Authority}: {ex.Message}";
                }
                catch (HttpRequestException ex) when (ex.HttpRequestError == HttpRequestError.ProxyTunnelError)
                {
                    // 407 на CONNECT приходит сюда, а не ответом.
                    if (ex.Message.Contains("407", StringComparison.Ordinal))
                        return string.IsNullOrEmpty(userName)
                            ? "Прокси требует логин и пароль (ответ 407)."
                            : "Прокси не принял логин или пароль (ответ 407).";
                    lastError = $"прокси отказал в соединении: {ex.Message}";
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                }
            }
            return $"Через прокси не открылся ни CDN, ни GitHub ({lastError}).";
        }

        /// <summary>
        /// Сначала через прокси; при ошибке прокси — напрямую (если не выключено
        /// манифестом). Повторяются только запросы без тела: тело уже прочитано первым
        /// обработчиком и второй раз не отправится. У лаунчера это все загрузки (GET);
        /// POST отчёта о сбое при ошибке прокси просто завершится ошибкой, как раньше.
        /// </summary>
        private sealed class ProxyFallbackHandler : DelegatingHandler
        {
            private readonly HttpMessageInvoker _direct;

            public ProxyFallbackHandler(HttpMessageHandler proxied, HttpMessageHandler direct) : base(proxied)
            {
                _direct = new HttpMessageInvoker(direct, disposeHandler: true);
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Uri? uri = request.RequestUri;
                bool viaProxy = uri != null && LauncherProxy.IsProxied(uri);
                bool canRetry = viaProxy && request.Content == null && LauncherProxy.DirectFallbackAllowed;

                if (canRetry && DateTime.UtcNow.Ticks < Interlocked.Read(ref _directUntilTicks))
                    return await _direct.SendAsync(request, ct).ConfigureAwait(false);

                HttpResponseMessage response;
                try
                {
                    response = await base.SendAsync(request, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (canRetry && !ct.IsCancellationRequested && IsProxyFailure(ex, viaProxy))
                {
                    LauncherLog.Write($"[Прокси] Ошибка прокси ({ex.GetType().Name}: {ex.Message}) — повтор напрямую");
                    return await SendDirectAsync(request, ct).ConfigureAwait(false);
                }

                if (canRetry && response.StatusCode == HttpStatusCode.ProxyAuthenticationRequired)
                {
                    LauncherLog.Write("[Прокси] Прокси требует авторизацию (407) — повтор напрямую. " +
                                      "Логин и пароль прокси задаются в «Настройки → Прокси».");
                    response.Dispose();
                    return await SendDirectAsync(request, ct).ConfigureAwait(false);
                }
                return response;
            }

            private async Task<HttpResponseMessage> SendDirectAsync(HttpRequestMessage request, CancellationToken ct)
            {
                // Обработчик с прокси мог дописать в запрос заголовок авторизации на
                // прокси (Basic для обычного HTTP-запроса через прокси). Напрямую он ушёл
                // бы серверу назначения — то есть логин и пароль прокси получил бы чужой сайт.
                request.Headers.ProxyAuthorization = null;
                var response = await _direct.SendAsync(request, ct).ConfigureAwait(false);
                Interlocked.Exchange(ref _directUntilTicks, (DateTime.UtcNow + DirectAfterProxyFailure).Ticks);
                return response;
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) _direct.Dispose();
                base.Dispose(disposing);
            }
        }
    }
}
