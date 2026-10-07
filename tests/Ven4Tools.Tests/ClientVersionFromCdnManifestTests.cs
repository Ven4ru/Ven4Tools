using System.Text;
using System.Text.Json;
using Ven4Tools.Launcher.Models;
using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Tests;

/// <summary>
/// Запись о версии клиента из одного подписанного version.json CDN — запасной путь,
/// когда списка релизов GitHub нет. Главное, что здесь защищается: запись появляется
/// только из манифеста с подтверждённой подписью и только вместе с SHA256 архива,
/// а поля манифеста ложатся в неё так же, как при подстановке поверх релиза GitHub.
/// </summary>
public sealed class ClientVersionFromCdnManifestTests
{
    private const string Sha = "485e3afb8dfceab1334b39fb55fb2276dc3383ba50234d557ba917df9bdb4520";

    // Блок client в том виде, в каком его выкладывает Tools/deploy-cdn-release.ps1.
    private const string ManifestJson = """
    {
      "client": {
        "version": "6.0.2",
        "zip_url": "https://cdn.ven4tools.ru/releases/Ven4Tools-Client-6.0.2.zip",
        "zip_fallback": "https://github.com/Ven4ru/Ven4Tools/releases/download/v6.0.2/Ven4Tools-Client-6.0.2.zip",
        "zip_mirror_hosting": "https://ven4tools.ru/releases/Ven4Tools-Client-6.0.2.zip",
        "zip_sha256": "485e3afb8dfceab1334b39fb55fb2276dc3383ba50234d557ba917df9bdb4520",
        "manifest_url": "https://cdn.ven4tools.ru/client-files/6.0.2/client-manifest.json",
        "manifest_signature_url": "https://cdn.ven4tools.ru/client-files/6.0.2/client-manifest.json.sig",
        "files_base_url": "https://cdn.ven4tools.ru/client-files/6.0.2/",
        "files_base_mirror_hosting": "https://ven4tools.ru/releases/client-files/6.0.2/"
      },
      "launcher": { "version": "3.9.0" },
      "cdn_ip": "138.16.152.133"
    }
    """;

    private static CdnVersionInfo Manifest(Action<CdnClientInfo>? change = null)
    {
        var info = JsonSerializer.Deserialize<CdnVersionInfo>(ManifestJson)!;
        change?.Invoke(info.Client!);
        return info;
    }

    private static string Fixture(string fileName) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName), Encoding.UTF8);

    [Fact]
    public void ПолныйМанифест_ДаётЗаписьСоВсемиПолями()
    {
        var version = ClientVersionMapper.BuildFromCdnManifest(Manifest());

        Assert.NotNull(version);
        Assert.Equal("6.0.2", version!.Version);
        Assert.True(version.IsLatest);
        Assert.Equal(Sha, version.ExpectedSha256);
        Assert.Equal("https://cdn.ven4tools.ru/releases/Ven4Tools-Client-6.0.2.zip", version.DownloadUrl);
        Assert.Equal("https://cdn.ven4tools.ru/releases/Ven4Tools-Client-6.0.2.zip", version.CdnUrl);
        Assert.Equal("https://ven4tools.ru/releases/Ven4Tools-Client-6.0.2.zip", version.MirrorHostingUrl);
        Assert.Equal(
            "https://github.com/Ven4ru/Ven4Tools/releases/download/v6.0.2/Ven4Tools-Client-6.0.2.zip",
            version.GithubUrl);
        Assert.Equal("https://cdn.ven4tools.ru/client-files/6.0.2/client-manifest.json", version.ManifestUrl);
        Assert.Equal(
            "https://cdn.ven4tools.ru/client-files/6.0.2/client-manifest.json.sig",
            version.ManifestSignatureUrl);
        Assert.Equal("https://cdn.ven4tools.ru/client-files/6.0.2/", version.FilesBaseUrl);
        Assert.Equal("https://ven4tools.ru/releases/client-files/6.0.2/", version.FilesBaseMirrorHostingUrl);
    }

    [Fact]
    public void ЗаписьИзМанифеста_ПроходитПоВсейЦепочкеИсточников()
    {
        // Запись того же типа, что и из релиза GitHub, — значит, и цепочка источников
        // строится из неё обычным образом: CDN, CDN по прямому IP, зеркало, GitHub.
        var version = ClientVersionMapper.BuildFromCdnManifest(Manifest())!;
        using var normal = new HttpClient();
        using var pinned = new HttpClient();

        var candidates = FallbackDownloader.BuildCandidates(
            DownloadSource.Auto, version.CdnUrl, version.MirrorHostingUrl,
            version.GithubUrl ?? version.DownloadUrl, normal, pinned);

        Assert.Equal(
            new[] { "CDN", "CDN (прямой IP)", "Хостинг", "GitHub" },
            candidates.Select(c => c.SourceLabel).ToArray());
        Assert.All(candidates, c => Assert.True(DownloadValidator.IsAllowedDownloadHost(c.Url)));
    }

    [Fact]
    public void МанифестНеПолучен_ЗаписиНет()
    {
        // null — CDN не ответил либо подпись version.json не подтверждена.
        Assert.Null(ClientVersionMapper.BuildFromCdnManifest(null));
    }

    [Fact]
    public void БезПодписи_МанифестНеРазбирается_ЗаписиНет()
    {
        Assert.Null(CdnService.ParseVerified(ManifestJson, signature: null));
        Assert.Null(ClientVersionMapper.BuildFromCdnManifest(CdnService.ParseVerified(ManifestJson, null)));
    }

    [Fact]
    public void ЧужаяПодпись_МанифестНеРазбирается_ЗаписиНет()
    {
        // Настоящая подпись, но другого текста: правдоподобный манифест с блоком
        // client, к которому приложена подпись от тестового образца.
        string foreignSignature = Fixture("version-manifest-sample.json.sig");

        var parsed = CdnService.ParseVerified(ManifestJson, foreignSignature);

        Assert.Null(parsed);
        Assert.Null(ClientVersionMapper.BuildFromCdnManifest(parsed));
    }

    [Fact]
    public void ПодписанныйМанифестБезБлокаClient_ЗаписиНет()
    {
        // Образец подписан рабочим ключом и проверку подписи проходит, но блока
        // client в нём нет — подпись сама по себе записи не создаёт.
        var parsed = CdnService.ParseVerified(
            Fixture("version-manifest-sample.json"), Fixture("version-manifest-sample.json.sig"));

        Assert.NotNull(parsed);
        Assert.Null(parsed!.Client);
        Assert.Null(ClientVersionMapper.BuildFromCdnManifest(parsed));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("485e3afb")]                                                           // обрезан
    [InlineData("zz5e3afb8dfceab1334b39fb55fb2276dc3383ba50234d557ba917df9bdb4520")] // не hex
    public void БезКорректногоSha256_ЗаписиНет(string? sha256)
    {
        // На этом пути нет записи релиза с GitHub: без хеша из подписанного манифеста
        // скачанное нечем проверить, поэтому запись не строится вовсе.
        Assert.Null(ClientVersionMapper.BuildFromCdnManifest(Manifest(c => c.ZipSha256 = sha256)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void БезВерсии_ЗаписиНет(string? version)
    {
        Assert.Null(ClientVersionMapper.BuildFromCdnManifest(Manifest(c => c.Version = version)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("http://cdn.ven4tools.ru/releases/Ven4Tools-Client-6.0.2.zip")]   // не HTTPS
    [InlineData("https://evil.example/releases/Ven4Tools-Client-6.0.2.zip")]      // чужой хост
    [InlineData("https://ven4tools.ru/api/db.php")]                               // сайт, но не /releases/
    public void СсылкаНаАрхивПустаИлиНедоверенная_ЗаписиНет(string? zipUrl)
    {
        Assert.Null(ClientVersionMapper.BuildFromCdnManifest(Manifest(c => c.ZipUrl = zipUrl)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("https://evil.example/Ven4Tools-Client-6.0.2.zip")]
    public void РезервнаяСсылкаGitHubПустаИлиНедоверенная_ЗаписьБезНеё(string? zipFallback)
    {
        var version = ClientVersionMapper.BuildFromCdnManifest(Manifest(c => c.ZipFallback = zipFallback));

        Assert.NotNull(version);
        Assert.Null(version!.GithubUrl);
        Assert.Equal(Sha, version.ExpectedSha256);
    }

    [Fact]
    public void МанифестБезАдресовДельты_ЗаписьЕсть_ДельтаНедоступна()
    {
        // Релиз без файлового манифеста: обновление пойдёт полным архивом.
        var version = ClientVersionMapper.BuildFromCdnManifest(Manifest(c =>
        {
            c.ManifestUrl = null;
            c.ManifestSignatureUrl = null;
            c.FilesBaseUrl = null;
            c.FilesBaseMirrorHosting = null;
        }));

        Assert.NotNull(version);
        Assert.Null(version!.ManifestUrl);
        Assert.Null(version.FilesBaseUrl);
        Assert.Equal(Sha, version.ExpectedSha256);
    }

    [Fact]
    public void ПредварительнаяВерсия_НеПомечаетсяТекущей()
    {
        // Как и в списке из релизов GitHub: предварительная сборка сама к установке
        // и обновлению не предлагается.
        var version = ClientVersionMapper.BuildFromCdnManifest(Manifest(c => c.Version = "6.1.0-beta.1"));

        Assert.NotNull(version);
        Assert.False(version!.IsLatest);
    }

    [Fact]
    public void Подстановка_ПоверхЗаписиРелизаТойЖеВерсии_СохраняетСсылкуGitHub()
    {
        // Обычный путь (GitHub доступен): запись релиза дополняется из манифеста,
        // а ссылка из релиза остаётся крайним резервом.
        const string releaseUrl = "https://github.com/Ven4ru/Ven4Tools/releases/download/v6.0.2/release-asset.zip";
        var info = new ClientVersionInfo { Version = "6.0.2", DownloadUrl = releaseUrl, GithubUrl = releaseUrl };

        bool applied = ClientVersionMapper.ApplyCdnClientInfo(info, Manifest().Client);

        Assert.True(applied);
        Assert.Equal("https://cdn.ven4tools.ru/releases/Ven4Tools-Client-6.0.2.zip", info.DownloadUrl);
        Assert.Equal(releaseUrl, info.GithubUrl);
        Assert.Equal(Sha, info.ExpectedSha256);
    }

    [Fact]
    public void Подстановка_ДляДругойВерсии_НичегоНеМеняет()
    {
        // Хеш манифеста относится к архиву 6.0.2 — к записи 6.0.3 он неприменим.
        const string releaseUrl = "https://github.com/Ven4ru/Ven4Tools/releases/download/v6.0.3/release-asset.zip";
        var info = new ClientVersionInfo { Version = "6.0.3", DownloadUrl = releaseUrl, GithubUrl = releaseUrl };

        bool applied = ClientVersionMapper.ApplyCdnClientInfo(info, Manifest().Client);

        Assert.False(applied);
        Assert.Equal(releaseUrl, info.DownloadUrl);
        Assert.Null(info.CdnUrl);
        Assert.Null(info.ExpectedSha256);
        Assert.Null(info.ManifestUrl);
    }

    [Fact]
    public void Подстановка_БезБлокаClient_НичегоНеМеняет()
    {
        var info = new ClientVersionInfo { Version = "6.0.2", DownloadUrl = "https://github.com/x/y.zip" };

        Assert.False(ClientVersionMapper.ApplyCdnClientInfo(info, null));
        Assert.Null(info.ExpectedSha256);
    }
}
