using System.ComponentModel;
using System.Net.Http;
using Ven4Tools.Launcher.Helpers;
using Ven4Tools.Launcher.Models;
using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Tests;

/// <summary>
/// Чистая логика функций лаунчера 3.12 «доверие и сеть»: антивирус или нет доступа (Л3),
/// трактовка Smart App Control (Л5), разбор адреса прокси и ошибок прокси (Л6), сверка
/// хешей (Л7), итог диагностики соединения и очистка отчёта (Л8). Сети, WMI и реестра
/// здесь нет — только функции, по которым принимается решение.
/// </summary>
public sealed class LauncherTrustNetworkTests
{
    private const string HashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string HashB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    // ---------- Л3: антивирус или нет доступа ----------

    [Fact]
    public void Classify_VirusErrorCode_IsConfirmedAntivirus()
    {
        Assert.Equal(FileProblemKind.AntivirusConfirmed,
            AntivirusAdvisor.Classify(new FileProblemEvidence { Win32Error = 225 }));
        Assert.Equal(FileProblemKind.AntivirusConfirmed,
            AntivirusAdvisor.Classify(new FileProblemEvidence { Win32Error = 226, FolderWritable = false }));
    }

    [Fact]
    public void Classify_DefenderDetection_IsConfirmedAntivirus()
    {
        Assert.Equal(FileProblemKind.AntivirusConfirmed,
            AntivirusAdvisor.Classify(new FileProblemEvidence { FileVanished = true, DefenderDetectionMatched = true }));
    }

    [Fact]
    public void Classify_AccessDeniedAndFolderNotWritable_IsAcl()
    {
        Assert.Equal(FileProblemKind.AccessDenied,
            AntivirusAdvisor.Classify(new FileProblemEvidence { Win32Error = 5, FolderWritable = false }));
    }

    [Fact]
    public void Classify_AccessDeniedButFolderWritable_LooksLikeAntivirus()
    {
        Assert.Equal(FileProblemKind.AntivirusLikely,
            AntivirusAdvisor.Classify(new FileProblemEvidence { Win32Error = 5, FolderWritable = true }));
    }

    [Fact]
    public void Classify_AccessDeniedWithoutProbe_IsHonestUnknown()
    {
        Assert.Equal(FileProblemKind.Unknown,
            AntivirusAdvisor.Classify(new FileProblemEvidence { Win32Error = 5 }));
    }

    [Fact]
    public void Classify_VanishedFile_LooksLikeAntivirus_NoErrorIsNone()
    {
        Assert.Equal(FileProblemKind.AntivirusLikely, AntivirusAdvisor.Classify(new FileProblemEvidence { FileVanished = true }));
        Assert.Equal(FileProblemKind.None, AntivirusAdvisor.Classify(new FileProblemEvidence()));
        Assert.Equal(FileProblemKind.Unknown, AntivirusAdvisor.Classify(new FileProblemEvidence { Win32Error = 112 }));
    }

    [Fact]
    public void ExtractWin32Error_ReadsCodesFromTypicalExceptions()
    {
        Assert.Equal(5, AntivirusAdvisor.ExtractWin32Error(new UnauthorizedAccessException("x")));
        Assert.Equal(225, AntivirusAdvisor.ExtractWin32Error(new IOException("virus", unchecked((int)0x800700E1))));
        Assert.Equal(4551, AntivirusAdvisor.ExtractWin32Error(new Win32Exception(4551)));
        Assert.Equal(225, AntivirusAdvisor.ExtractWin32Error(
            new InvalidOperationException("wrap", new IOException("virus", unchecked((int)0x800700E1)))));
        Assert.Null(AntivirusAdvisor.ExtractWin32Error(new InvalidOperationException("nothing")));
    }

    [Theory]
    [InlineData(@"file:_C:\Games\Ven4Tools\Ven4Tools.exe", @"C:\Games\Ven4Tools", true)]
    [InlineData(@"containerfile:_C:\Games\Ven4Tools\pack.zip->inner.dll", @"C:\Games\Ven4Tools\", true)]
    [InlineData(@"file:_c:\games\ven4tools\Ven4Tools.dll", @"C:\Games\Ven4Tools", true)]
    [InlineData(@"file:_C:\Games\Ven4Tools2\Ven4Tools.exe", @"C:\Games\Ven4Tools", false)]
    [InlineData(@"CmdLine:_C:\Windows\System32\cmd.exe /c x", @"C:\Games\Ven4Tools", false)]
    [InlineData(null, @"C:\Games\Ven4Tools", false)]
    public void ResourceIsInFolder_RespectsDirectoryBoundary(string? resource, string folder, bool expected)
    {
        Assert.Equal(expected, AntivirusAdvisor.ResourceIsInFolder(resource, folder));
    }

    [Theory]
    [InlineData(397568u, true)]   // 0x061100 — Defender включён
    [InlineData(393472u, false)]  // 0x060100 — выключен
    [InlineData(266240u, true)]   // 0x041000 — сторонний антивирус включён
    public void IsProductEnabled_ReadsSecondByte(uint state, bool expected)
    {
        Assert.Equal(expected, AntivirusAdvisor.IsProductEnabled(state));
    }

    [Fact]
    public void BuildLinks_PutsInstalledAntivirusFirst_AndVirusTotalOnlyByHash()
    {
        var links = AntivirusAdvisor.BuildLinks(HashA.ToUpperInvariant(), new[] { "Kaspersky Free" });
        Assert.Contains("opentip.kaspersky.com/" + HashA, links[0].Url);
        Assert.Contains(links, l => l.Url == "https://www.virustotal.com/gui/file/" + HashA);
        Assert.All(links, l => Assert.True(SafeLinkOpener.IsAllowed(l.Url), l.Url));

        var noHash = AntivirusAdvisor.BuildLinks(null, null);
        Assert.DoesNotContain(noHash, l => l.Url.Contains("virustotal", StringComparison.Ordinal));
        Assert.Contains(noHash, l => l.Url.Contains("drweb", StringComparison.Ordinal));
        Assert.Contains(noHash, l => l.Url.Contains("microsoft.com", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildExplanation_NeverSuggestsAddingExclusionAutomatically()
    {
        var (title, body) = AntivirusAdvisor.BuildExplanation(FileProblemKind.AntivirusConfirmed,
            new[] { new AntivirusProduct("Windows Defender", true) },
            new[] { new DefenderDetection("Trojan:Win32/Test", @"file:_C:\x\Ven4Tools.exe", null) }, "Ven4Tools.exe");
        Assert.Contains("антивирус", title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Trojan:Win32/Test", body);
        Assert.Contains("Windows Defender", body);
        Assert.Contains("ничего не добавляет в исключения", body);

        var (aclTitle, aclBody) = AntivirusAdvisor.BuildExplanation(FileProblemKind.AccessDenied, null, null, null);
        Assert.Contains("Нет доступа", aclTitle);
        Assert.Contains("не антивирус", aclBody);
    }

    [Fact]
    public void BuildExplanation_UnknownAntivirusList_SaysNoData()
    {
        var (_, body) = AntivirusAdvisor.BuildExplanation(FileProblemKind.AntivirusLikely, null, null, "Ven4Tools.dll");
        Assert.Contains("нет данных", body);
    }

    // ---------- Л5: Smart App Control ----------

    [Fact]
    public void Interpret_MapsRegistryValuesInOnePlace()
    {
        Assert.Equal(SmartAppControlState.NotPresent, SmartAppControl.Interpret(null));
        Assert.Equal(SmartAppControlState.Off, SmartAppControl.Interpret(0));
        Assert.Equal(SmartAppControlState.On, SmartAppControl.Interpret(1));
        Assert.Equal(SmartAppControlState.Evaluation, SmartAppControl.Interpret(2));
        Assert.Equal(SmartAppControlState.Unknown, SmartAppControl.Interpret(7));
        Assert.Equal(SmartAppControlState.Unknown, SmartAppControl.Interpret("1"));
    }

    [Fact]
    public void UnverifiedInterpretation_UsesNeutralWording_AndWarnsForBothStates()
    {
        Assert.Equal(SmartAppControl.Describe(SmartAppControlState.On, false),
                     SmartAppControl.Describe(SmartAppControlState.Evaluation, false));
        Assert.Contains("или", SmartAppControl.Describe(SmartAppControlState.On, false));
        Assert.True(SmartAppControl.MayBlock(SmartAppControlState.Evaluation, false));
        Assert.False(SmartAppControl.MayBlock(SmartAppControlState.Evaluation, true));
        Assert.True(SmartAppControl.MayBlock(SmartAppControlState.On, true));
        Assert.False(SmartAppControl.MayBlock(SmartAppControlState.Off, false));
        Assert.False(SmartAppControl.MayBlock(SmartAppControlState.NotPresent, false));
        Assert.NotEqual(SmartAppControl.Describe(SmartAppControlState.On, true),
                        SmartAppControl.Describe(SmartAppControlState.Evaluation, true));
    }

    [Fact]
    public void LaunchBlocked_RecognizesPolicyCodes_AndExplainsHonestly()
    {
        Assert.True(SmartAppControl.IsPolicyBlock(4551));
        Assert.True(SmartAppControl.IsPolicyBlock(1260));
        Assert.False(SmartAppControl.IsPolicyBlock(5));
        Assert.False(SmartAppControl.IsPolicyBlock(null));

        string sac = SmartAppControl.BuildLaunchBlockedText(4551, SmartAppControlState.On, null);
        Assert.Contains("4551", sac);
        Assert.Contains("нет данных", sac);
        Assert.Contains("ничего не выключает", sac);

        string applocker = SmartAppControl.BuildLaunchBlockedText(1260, SmartAppControlState.Off, null);
        Assert.Contains("администратор", applocker);
        Assert.Contains("3077", SmartAppControl.BuildLaunchBlockedText(4551, SmartAppControlState.Off, true));
    }

    // ---------- Л6: прокси ----------

    [Theory]
    [InlineData("proxy.company.ru:3128", "http://proxy.company.ru:3128/")]
    [InlineData("http://10.0.0.1:8080", "http://10.0.0.1:8080/")]
    [InlineData(" http://proxy:80/ ", "http://proxy/")]
    public void TryParseAddress_AcceptsHostAndPort(string input, string expected)
    {
        Assert.True(LauncherProxy.TryParseAddress(input, out var uri));
        Assert.Equal(expected, uri!.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("proxy.company.ru")]            // без порта — не угадываем
    [InlineData("https://proxy:443")]           // только http-прокси
    [InlineData("socks5://proxy:1080")]
    [InlineData("http://user:pass@proxy:3128")] // логин — в отдельном поле, не в адресе
    [InlineData("http://proxy:3128/path")]
    public void TryParseAddress_RejectsUnsupported(string input)
    {
        Assert.False(LauncherProxy.TryParseAddress(input, out _));
    }

    [Fact]
    public void IsProxyFailure_OnlyProxyErrorsAndOnlyWhenProxied()
    {
        var tunnel = new HttpRequestException(HttpRequestError.ProxyTunnelError, "407");
        var connect = new HttpRequestException(HttpRequestError.ConnectionError, "refused");
        var status = new HttpRequestException(HttpRequestError.InvalidResponse, "bad");
        Assert.True(LauncherHttp.IsProxyFailure(tunnel, viaProxy: true));
        Assert.True(LauncherHttp.IsProxyFailure(connect, viaProxy: true));
        Assert.False(LauncherHttp.IsProxyFailure(connect, viaProxy: false));
        Assert.False(LauncherHttp.IsProxyFailure(status, viaProxy: true));
        Assert.False(LauncherHttp.IsProxyFailure(new TaskCanceledException(), viaProxy: true));
    }

    // ---------- Л7: подлинность ----------

    [Fact]
    public void Compare_NormalizesAndDistinguishesNoData()
    {
        Assert.Equal(HashCheck.Match, AuthenticityChecker.Compare(HashA.ToUpperInvariant(), "sha256:" + HashA));
        Assert.Equal(HashCheck.Mismatch, AuthenticityChecker.Compare(HashA, HashB));
        Assert.Equal(HashCheck.NoData, AuthenticityChecker.Compare(HashA, null));
        Assert.Equal(HashCheck.NoData, AuthenticityChecker.Compare(HashA, "не-хеш"));
        Assert.Equal(HashCheck.NoData, AuthenticityChecker.Compare(null, HashA));
    }

    [Fact]
    public void Summarize_MismatchWinsOverMatches()
    {
        var sources = new[]
        {
            new SourceCheck("CDN", HashCheck.Match, HashA, null),
            new SourceCheck("GitHub", HashCheck.Mismatch, HashB, null),
        };
        Assert.Contains("НЕ СОВПАДАЕТ", AuthenticityChecker.Summarize(HashA, sources));
    }

    [Fact]
    public void Summarize_CountsIndependentSourcesHonestly()
    {
        var two = new[] { new SourceCheck("CDN", HashCheck.Match, HashA, null), new SourceCheck("GitHub", HashCheck.Match, HashA, null) };
        var one = new[] { new SourceCheck("CDN", HashCheck.Match, HashA, null), new SourceCheck("GitHub", HashCheck.NoData, null, "нет") };
        var none = new[] { new SourceCheck("CDN", HashCheck.NoData, null, null) };
        Assert.Contains("двумя", AuthenticityChecker.Summarize(HashA, two));
        Assert.Contains("одним", AuthenticityChecker.Summarize(HashA, one));
        Assert.StartsWith("Нет данных", AuthenticityChecker.Summarize(HashA, none));
        Assert.StartsWith("Нет данных", AuthenticityChecker.Summarize(null, two));
    }

    [Fact]
    public void FindInSha256Sums_ReadsBothFormats()
    {
        string sums = $"{HashA.ToUpperInvariant()}  Ven4Tools.Setup-3.12.0.exe\r\n{HashB} *Ven4Tools.Launcher.exe\n";
        Assert.Equal(HashA, AuthenticityChecker.FindInSha256Sums(sums, "Ven4Tools.Setup-3.12.0.exe"));
        Assert.Equal(HashB, AuthenticityChecker.FindInSha256Sums(sums, "ven4tools.launcher.exe"));
        Assert.Null(AuthenticityChecker.FindInSha256Sums(sums, "Ven4Tools.exe"));
        Assert.Null(AuthenticityChecker.FindInSha256Sums(null, "Ven4Tools.exe"));
    }

    [Fact]
    public void DescribePublication_ReportsConsistency()
    {
        Assert.Contains("совпадают", AuthenticityChecker.DescribePublication("Архив", HashA, HashA));
        Assert.Contains("РАЗНЫЕ", AuthenticityChecker.DescribePublication("Архив", HashA, HashB));
        Assert.Contains("нет данных", AuthenticityChecker.DescribePublication("Архив", HashA, null));
    }

    [Fact]
    public void KeyFingerprint_IsStableHexGroups()
    {
        string fp1 = AuthenticityChecker.KeyFingerprint(UpdateManifestVerifier.PublicKeyPem);
        string fp2 = AuthenticityChecker.KeyFingerprint(UpdateManifestVerifier.PublicKeyPem);
        Assert.Equal(fp1, fp2);
        Assert.Equal(16, fp1.Split(' ').Length);
        Assert.NotEqual(fp1, AuthenticityChecker.KeyFingerprint(ClientManifestVerifier.PublicKeyPem));
    }

    [Fact]
    public void LauncherFlags_AbsentObjectMeansDefaults()
    {
        var info = System.Text.Json.JsonSerializer.Deserialize<CdnVersionInfo>("{\"launcher\":{\"version\":\"3.12.0\"}}")!;
        Assert.Null(info.LauncherFlags);
        var withFlags = System.Text.Json.JsonSerializer.Deserialize<CdnVersionInfo>(
            "{\"launcher_flags\":{\"proxy_manual\":false},\"launcher\":{\"installed_exe_sha256\":\"" + HashA + "\"}}")!;
        Assert.False(withFlags.LauncherFlags!.ProxyManual);
        Assert.Null(withFlags.LauncherFlags.ProxyDirectFallback);
        Assert.Equal(HashA, withFlags.Launcher!.InstalledExeSha256);
    }

    [Theory]
    [InlineData("https://www.virustotal.com/gui/file/" + HashA, true)]
    [InlineData("https://opentip.kaspersky.com/x/", true)]
    [InlineData("http://www.virustotal.com/gui/file/x", false)]
    [InlineData("https://evil.example/", false)]
    [InlineData("file:///C:/Windows/notepad.exe", false)]
    [InlineData("https://github.com.evil.example/", false)]
    public void SafeLinkOpener_AllowsOnlyKnownHttpsSites(string url, bool expected)
    {
        Assert.Equal(expected, SafeLinkOpener.IsAllowed(url));
    }

    // ---------- Л8: диагностика соединения ----------

    private static SourceProbe Probe(DiagSource s, bool ok, bool? dns = true, long bytes = 1024 * 1024, bool stalled = false)
        => new()
        {
            Source = s,
            Name = s.ToString(),
            Host = "h",
            DnsOk = s == DiagSource.CdnDirectIp ? null : dns,
            TcpOk = ok,
            TlsOk = ok,
            HttpStatus = ok || stalled ? 206 : null,
            HttpError = ok || stalled ? null : "нет ответа",
            BytesRead = ok || stalled ? bytes : 0,
            Seconds = 2,
            Stalled = stalled,
            SpeedMeasured = true,
        };

    [Fact]
    public void Diagnostics_AllOk_NamesFastest()
    {
        var probes = new[] { Probe(DiagSource.CdnDomain, true), Probe(DiagSource.CdnDirectIp, true), Probe(DiagSource.Mirror, true), Probe(DiagSource.GitHub, true) };
        var list = ConnectionDiagnostics.Summarize(probes, TimeSpan.FromSeconds(3));
        Assert.Single(list);
        Assert.Contains("Все источники", list[0]);
    }

    [Fact]
    public void Diagnostics_CdnCutMirrorWorks()
    {
        var probes = new[]
        {
            Probe(DiagSource.CdnDomain, false, stalled: true, bytes: 16 * 1024),
            Probe(DiagSource.CdnDirectIp, false, stalled: true, bytes: 16 * 1024),
            Probe(DiagSource.Mirror, true),
            Probe(DiagSource.GitHub, true),
        };
        var list = ConnectionDiagnostics.Summarize(probes, null);
        Assert.Contains(list, s => s.Contains("Провайдер режет CDN, работает зеркало"));
        Assert.Contains(list, s => s.Contains("~16 КБ"));
    }

    [Fact]
    public void Diagnostics_DnsBlockOfCdnDomain_IsDetected()
    {
        var probes = new[]
        {
            Probe(DiagSource.CdnDomain, false, dns: false),
            Probe(DiagSource.CdnDirectIp, true),
            Probe(DiagSource.Mirror, true),
            Probe(DiagSource.GitHub, true),
        };
        Assert.Contains(ConnectionDiagnostics.Summarize(probes, null), s => s.Contains("DNS не находит cdn.ven4tools.ru"));
    }

    [Fact]
    public void Diagnostics_NothingWorks_SuggestsWifi()
    {
        var probes = new[] { Probe(DiagSource.CdnDomain, false), Probe(DiagSource.CdnDirectIp, false), Probe(DiagSource.Mirror, false), Probe(DiagSource.GitHub, false) };
        var list = ConnectionDiagnostics.Summarize(probes, null);
        Assert.Contains(list, s => s.Contains("Wi-Fi"));
    }

    [Fact]
    public void Diagnostics_Proxy407AndClockSkew_ComeFirst()
    {
        var p = Probe(DiagSource.CdnDomain, false);
        p.Proxy407 = true;
        p.HttpStatus = 407;
        var list = ConnectionDiagnostics.Summarize(new[] { p, Probe(DiagSource.GitHub, false) }, TimeSpan.FromDays(3));
        Assert.Contains("407", list[0]);
        Assert.Contains("отстают на 3 дн.", list[1]);
    }

    [Fact]
    public void Diagnostics_GitHubOnlyDown_IsNotAlarming()
    {
        var probes = new[] { Probe(DiagSource.CdnDomain, true), Probe(DiagSource.CdnDirectIp, true), Probe(DiagSource.Mirror, true), Probe(DiagSource.GitHub, false) };
        Assert.Contains(ConnectionDiagnostics.Summarize(probes, null), s => s.Contains("GitHub недоступен") && s.Contains("не мешает"));
    }

    [Fact]
    public void ClockSkew_UsesMedian_AndIgnoresSmallDrift()
    {
        var now = DateTimeOffset.UtcNow;
        var probes = new[]
        {
            new SourceProbe { ServerDate = now.AddDays(2), LocalAtResponse = now },
            new SourceProbe { ServerDate = now.AddDays(2).AddSeconds(1), LocalAtResponse = now },
            new SourceProbe { ServerDate = now.AddYears(5), LocalAtResponse = now }, // выброс
        };
        var skew = ConnectionDiagnostics.EstimateClockSkew(probes)!.Value;
        Assert.InRange(skew.TotalDays, 1.9, 2.1);
        Assert.Null(ConnectionDiagnostics.DescribeClockSkew(TimeSpan.FromMinutes(2)));
        Assert.Contains("спешат", ConnectionDiagnostics.DescribeClockSkew(TimeSpan.FromHours(-3))!);
        Assert.Null(ConnectionDiagnostics.EstimateClockSkew(Array.Empty<SourceProbe>()));
    }

    [Fact]
    public void Report_HasNoUserMachineOrProfilePath()
    {
        string user = Environment.UserName;
        string machine = Environment.MachineName;
        var r = new DiagnosticsResult { ProxyDescription = "системный (прокси не задан — напрямую)" };
        var p = Probe(DiagSource.Mirror, false);
        p.HttpError = $"ошибка в C:\\Users\\{user}\\AppData\\Local\\Temp на {machine}";
        r.Probes.Add(p);
        r.Conclusions = ConnectionDiagnostics.Summarize(r.Probes, null);

        string report = ConnectionDiagnostics.BuildReport(r, "3.12.0", "10.0.26100.0");
        if (user.Length >= 3) Assert.DoesNotContain(user, report, StringComparison.OrdinalIgnoreCase);
        if (machine.Length >= 3) Assert.DoesNotContain(machine, report, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Итог:", report);
        Assert.Contains("3.12.0", report);
    }
}
