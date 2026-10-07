using Ven4Tools.Services;

namespace Ven4Tools.Tests;

/// <summary>
/// Переносной офлайн-набор: что кладётся в папку набора и как его файл ответа
/// читается тихим режимом. Настоящий клиент здесь не копируется — вместо него
/// временная папка с файлом Ven4Tools.exe.
/// </summary>
public sealed class OfflineKitBuilderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "v4t-kit-" + Guid.NewGuid().ToString("N"));
    private readonly string _client;
    private readonly string _kit;

    public OfflineKitBuilderTests()
    {
        _client = Path.Combine(_root, "client");
        _kit = Path.Combine(_root, "flash");
        Directory.CreateDirectory(Path.Combine(_client, "Data"));
        Directory.CreateDirectory(Path.Combine(_client, "Logs"));
        File.WriteAllText(Path.Combine(_client, "Ven4Tools.exe"), "exe");
        File.WriteAllText(Path.Combine(_client, "Ven4Tools.dll"), "dll");
        File.WriteAllText(Path.Combine(_client, "Ven4Tools.pdb"), "pdb");
        File.WriteAllText(Path.Combine(_client, "Data", "master.json"), "catalog");
        File.WriteAllText(Path.Combine(_client, "Logs", "app.log"), "log");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Набор_СодержитКлиентФайлОтветаИФайлЗапуска()
    {
        var result = OfflineKitBuilder.Build(_kit, new[] { "firefox", "7zip" }, _client);

        Assert.Equal(2, result.Apps);
        Assert.True(File.Exists(Path.Combine(_kit, "Ven4Tools", "Ven4Tools.exe")));
        Assert.True(File.Exists(Path.Combine(_kit, OfflineKitBuilder.AnswerFileName)));
        Assert.True(File.Exists(Path.Combine(_kit, OfflineKitBuilder.LauncherFileName)));
        Assert.True(File.Exists(Path.Combine(_kit, OfflineKitBuilder.ReadmeFileName)));
    }

    [Fact]
    public void КэшКаталогаЕдетСКлиентом_АЖурналыИОтладочныеФайлыНет()
    {
        OfflineKitBuilder.Build(_kit, new[] { "firefox" }, _client);

        // Установщики набора скачаны по контрольным суммам этого каталога — без него
        // на новом компьютере они не прошли бы проверку по встроенной копии.
        Assert.True(File.Exists(Path.Combine(_kit, "Ven4Tools", "Data", "master.json")));
        Assert.False(Directory.Exists(Path.Combine(_kit, "Ven4Tools", "Logs")));
        Assert.False(File.Exists(Path.Combine(_kit, "Ven4Tools", "Ven4Tools.pdb")));
    }

    [Fact]
    public void ФайлОтвета_ЧитаетсяТихимРежимом_ИКэшИщетсяРядомСНим()
    {
        OfflineKitBuilder.Build(_kit, new[] { "firefox", "7zip" }, _client);
        string answerFile = Path.Combine(_kit, OfflineKitBuilder.AnswerFileName);

        var status = UnattendedCommandLine.Parse(
            new[] { "--answer-file", answerFile }, File.ReadAllText, out var request, out string error);

        Assert.True(status == UnattendedCommandLine.ParseStatus.Ok, error);
        Assert.Equal(new[] { "firefox", "7zip" }, request!.AppIds);
        Assert.Equal(Path.GetFullPath(_kit), request.OfflineCachePath);
        // Установку с флешки запускает человек: окно и вопросы остаются.
        Assert.False(request.Silent);
        Assert.False(request.AllowPackageManagers);
    }

    [Fact]
    public void ФайлЗапуска_ТолькоASCII_ИПутиОтСвоейПапки()
    {
        OfflineKitBuilder.Build(_kit, new[] { "firefox" }, _client);

        byte[] bytes = File.ReadAllBytes(Path.Combine(_kit, OfflineKitBuilder.LauncherFileName));
        string text = System.Text.Encoding.ASCII.GetString(bytes);

        Assert.All(bytes, b => Assert.True(b < 128, "В пакетном файле не должно быть символов вне ASCII."));
        Assert.Contains(@"%~dp0Ven4Tools\Ven4Tools.exe", text);
        Assert.Contains("--answer-file \"%~dp0" + OfflineKitBuilder.AnswerFileName + "\"", text);
    }

    [Fact]
    public void ПустойКэш_НаборНеСобирается()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => OfflineKitBuilder.Build(_kit, Array.Empty<string>(), _client));

        Assert.Contains("кэше", ex.Message);
        Assert.False(Directory.Exists(_kit));
    }

    [Theory]
    [InlineData("")]        // папка набора — сама папка клиента
    [InlineData("kit")]     // папка набора внутри папки клиента
    public void НаборВнутриПапкиКлиента_Отклоняется(string subfolder)
    {
        // Копия клиента легла бы в саму себя и при обходе росла бы без конца.
        string kitRoot = subfolder.Length == 0 ? _client : Path.Combine(_client, subfolder);

        Assert.Throws<InvalidOperationException>(
            () => OfflineKitBuilder.Build(kitRoot, new[] { "firefox" }, _client));
    }

    /// <summary>
    /// Кэш по умолчанию лежит в %LocalAppData%\Ven4Tools, а клиент — в его подпапке.
    /// Раньше при такой раскладке пропускались все файлы клиента, и набор «собирался» без него.
    /// </summary>
    [Fact]
    public void КлиентВнутриПапкиНабора_КопируетсяЦеликом()
    {
        var result = OfflineKitBuilder.Build(_root, new[] { "firefox" }, _client);

        Assert.True(result.ClientFiles >= 2);
        Assert.True(File.Exists(Path.Combine(_root, "Ven4Tools", "Ven4Tools.exe")));
        Assert.True(File.Exists(Path.Combine(_root, "Ven4Tools", "Data", "master.json")));
    }

    [Theory]
    [InlineData("report", "C:\\\\Windows\\\\win.ini")]
    [InlineData("report", "..\\\\итог.json")]
    [InlineData("offlineCache", "C:\\\\чужая")]
    [InlineData("offlineCache", "..")]
    public void ПутиИзФайлаОтвета_НеВыходятЗаЕгоПапку(string field, string value)
    {
        string json = "{ \"apps\": [\"vlc\"], \"" + field + "\": \"" + value + "\" }";

        var status = UnattendedCommandLine.Parse(
            new[] { "--answer-file", Path.Combine(_root, "набор.json") }, _ => json, out _, out string error);

        Assert.Equal(UnattendedCommandLine.ParseStatus.Error, status);
        Assert.Contains("относительно самого файла", error);
    }

    [Fact]
    public void ПапкаОфлайнНабора_ИзКоманднойСтроки()
    {
        var status = UnattendedCommandLine.Parse(
            new[] { "--install", "V4T:vlc", "--offline-cache", @"E:\набор" }, _ => "", out var request, out _);

        Assert.Equal(UnattendedCommandLine.ParseStatus.Ok, status);
        Assert.Equal(@"E:\набор", request!.OfflineCachePath);
    }
}
