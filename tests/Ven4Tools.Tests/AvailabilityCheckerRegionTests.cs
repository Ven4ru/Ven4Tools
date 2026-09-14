using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Ven4Tools.Models;
using Ven4Tools.Services;

namespace Ven4Tools.Tests;

/// <summary>
/// Классификация RegionBlocked — таблица из docs/superpowers/specs/
/// 2026-09-10-catalog-region-availability-design.md. Приложение с Id, начинающимся
/// на "User." (см. AvailabilityChecker.CheckAppAvailabilityWithSize), и пустым
/// ChocoId гарантированно минует winget/choco — единственный сетевой вызов идёт
/// через подменный HttpMessageHandler на прямую ссылку InstallerUrls[0].
/// </summary>
public sealed class AvailabilityCheckerRegionTests
{
    private const string TestUrl = "https://vendor.example/app.exe";

    private static AppInfo AppWithUrl(string? regionNote = null) => new()
    {
        Id = "User.test-app",
        DisplayName = "Test App",
        InstallerUrls = new List<string> { TestUrl },
        RegionNote = regionNote
    };

    private static AvailabilityChecker CheckerReturning(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new HttpClient(new DelegateHandler(respond)));

    private static HttpResponseMessage Response(Uri finalUri, HttpStatusCode statusCode, string contentType)
    {
        var response = new HttpResponseMessage(statusCode)
        {
            RequestMessage = new HttpRequestMessage(HttpMethod.Head, finalUri),
            Content = new ByteArrayContent(new byte[] { 1, 2, 3 })
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return response;
    }

    [Fact]
    public async Task Успех_ДаётAvailable_ДажеССноской()
    {
        using var checker = CheckerReturning(request =>
            Response(request.RequestUri!, HttpStatusCode.OK, "application/octet-stream"));

        var (status, _) = await checker.CheckAppAvailabilityWithSize(
            AppWithUrl(regionNote: "разработчик ушёл из РФ, загрузка через VPN"));

        Assert.Equal(AvailabilityChecker.AvailabilityStatus.Available, status);
    }

    [Fact]
    public async Task Код451_ВсегдаRegionBlocked_ДажеБезСноски()
    {
        using var checker = CheckerReturning(request =>
            new HttpResponseMessage((HttpStatusCode)451) { RequestMessage = request });

        var (status, _) = await checker.CheckAppAvailabilityWithSize(AppWithUrl());

        Assert.Equal(AvailabilityChecker.AvailabilityStatus.RegionBlocked, status);
    }

    [Fact]
    public async Task Код403СоСноской_ДаётRegionBlocked()
    {
        using var checker = CheckerReturning(request =>
            new HttpResponseMessage(HttpStatusCode.Forbidden) { RequestMessage = request });

        var (status, _) = await checker.CheckAppAvailabilityWithSize(
            AppWithUrl(regionNote: "разработчик ушёл из РФ"));

        Assert.Equal(AvailabilityChecker.AvailabilityStatus.RegionBlocked, status);
    }

    [Fact]
    public async Task Код403БезСноски_ОстаётсяUnavailable_НеГадаем()
    {
        using var checker = CheckerReturning(request =>
            new HttpResponseMessage(HttpStatusCode.Forbidden) { RequestMessage = request });

        var (status, _) = await checker.CheckAppAvailabilityWithSize(AppWithUrl());

        Assert.Equal(AvailabilityChecker.AvailabilityStatus.Unavailable, status);
    }

    [Fact]
    public async Task Код405_ЗатемРанжированныйGetВозвращает451_ДаётRegionBlocked()
    {
        // M1: единственная непокрытая до этого теста ветка — HEAD 405 (community.
        // chocolatey.org и подобные не поддерживают HEAD), затем ranged GET получает
        // 451 (RFC 7725) вместо 2xx/206 — уходит в тот же ClassifyFailure, что и прямой
        // 451 на HEAD, и должен дать тот же вердикт.
        // Различаем два запроса по методу, а не по порядковому номеру вызова:
        // порядковый счётчик молча начал бы проверять не то, если бы в цепочку
        // добавился ещё один запрос (та же причина, что и в тестах каскада ниже).
        using var checker = CheckerReturning(request =>
            request.Method == HttpMethod.Head
                ? new HttpResponseMessage(HttpStatusCode.MethodNotAllowed) { RequestMessage = request }
                : new HttpResponseMessage((HttpStatusCode)451) { RequestMessage = request });

        var (status, _) = await checker.CheckAppAvailabilityWithSize(AppWithUrl());

        Assert.Equal(AvailabilityChecker.AvailabilityStatus.RegionBlocked, status);
    }

    [Fact]
    public async Task РедиректНаГеоЗаглушку_ДаётRegionBlocked()
    {
        // Смена хоста + HTML вместо бинарника — единственный сигнал геозаглушки,
        // который можно снять с уже полученного ответа без нового запроса.
        using var checker = CheckerReturning(_ =>
            Response(new Uri("https://geo-block.example/blocked.html"), HttpStatusCode.OK, "text/html"));

        var (status, _) = await checker.CheckAppAvailabilityWithSize(AppWithUrl());

        Assert.Equal(AvailabilityChecker.AvailabilityStatus.RegionBlocked, status);
    }

    [Fact]
    public async Task ОбычныйРедиректНаЗеркалоСБинарником_ОстаётсяAvailable()
    {
        // Смена хоста сама по себе не геоблок: легитимный CDN-редирект на зеркало
        // с тем же типом содержимого (бинарник) обязан остаться Available, как и
        // до появления региональной проверки.
        using var checker = CheckerReturning(_ =>
            Response(new Uri("https://mirror.example/app.exe"), HttpStatusCode.OK, "application/octet-stream"));

        var (status, _) = await checker.CheckAppAvailabilityWithSize(AppWithUrl());

        Assert.Equal(AvailabilityChecker.AvailabilityStatus.Available, status);
    }

    [Fact]
    public async Task RegionBlockedОтПрямойСсылки_ПереживаетНеудачныйChoco()
    {
        var app = AppWithUrl();
        app.ChocoId = "does-not-matter";
        using var checker = new AvailabilityChecker(new HttpClient(new DelegateHandler(request =>
        {
            // Запрос на прямую ссылку (HEAD) — 451. Любой другой хост — это запрос к
            // Chocolatey (GetChocoPackageInfo) — отвечаем неудачей (404). Различаем по
            // хосту, а не по порядковому номеру вызова: порядок запросов — деталь
            // реализации CheckAppAvailabilityWithSize, а не то, что тест обязан пинить.
            return request.RequestUri!.Host == new Uri(TestUrl).Host
                ? new HttpResponseMessage((HttpStatusCode)451) { RequestMessage = request }
                : new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request };
        })));

        var (status, _) = await checker.CheckAppAvailabilityWithSize(app);

        Assert.Equal(AvailabilityChecker.AvailabilityStatus.RegionBlocked, status);
    }

    [Fact]
    public async Task RegionBlockedОтПрямойСсылки_УступаетУспешномуChoco()
    {
        var app = AppWithUrl();
        app.ChocoId = "works-via-choco";
        using var checker = new AvailabilityChecker(new HttpClient(new DelegateHandler(request =>
        {
            // Запрос на прямую ссылку (HEAD) — 451. Любой другой хост — это запрос к
            // Chocolatey (GetChocoPackageInfo) — отвечаем успехом (206). Различаем по
            // хосту, а не по порядковому номеру вызова: порядок запросов — деталь
            // реализации CheckAppAvailabilityWithSize, а не то, что тест обязан пинить.
            return request.RequestUri!.Host == new Uri(TestUrl).Host
                ? new HttpResponseMessage((HttpStatusCode)451) { RequestMessage = request }
                : new HttpResponseMessage(HttpStatusCode.PartialContent) { RequestMessage = request };
        })));

        var (status, _) = await checker.CheckAppAvailabilityWithSize(app);

        Assert.Equal(AvailabilityChecker.AvailabilityStatus.Available, status);
    }

    private sealed class DelegateHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_respond(request));
    }
}
