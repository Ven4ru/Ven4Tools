using System.Xml.Linq;
using Ven4Tools.Services;

namespace Ven4Tools.Tests;

/// <summary>
/// Файл ответов для установки Windows: что в него попадает и, главное, чего в нём нет —
/// разметки дисков, пароля и обхода проверок.
/// </summary>
public sealed class WindowsAnswerFileBuilderTests : IDisposable
{
    private static readonly XNamespace Ns = "urn:schemas-microsoft-com:unattend";
    private static readonly XNamespace Wcm = "http://schemas.microsoft.com/WMIConfig/2002/State";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "v4t-unattend-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static WindowsAnswerFileBuilder.Options Options(
        string account = "Иван", string kitPath = "Ven4Tools-Kit", string architecture = "amd64") =>
        new(account, kitPath, "ru-RU", new[] { "ru-RU", "en-US" }, "Russian Standard Time", architecture);

    private static XDocument Parse(string xml) => XDocument.Parse(xml);

    [Fact]
    public void Файл_корректный_XML_в_UTF8_с_единственным_проходом_oobeSystem()
    {
        string xml = WindowsAnswerFileBuilder.Build(Options());

        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", xml);
        var passes = Parse(xml).Root!.Elements(Ns + "settings").Select(s => s.Attribute("pass")!.Value).ToList();
        Assert.Equal(new[] { "oobeSystem" }, passes);
    }

    [Fact]
    public void В_файле_нет_разметки_дисков_ключей_автовхода_и_обхода_проверок()
    {
        string xml = WindowsAnswerFileBuilder.Build(Options());

        foreach (string forbidden in new[]
                 {
                     "windowsPE", "DiskConfiguration", "WillWipeDisk", "ImageInstall", "ProductKey",
                     "AutoLogon", "LabConfig", "BypassTPMCheck", "BypassSecureBootCheck", "BypassNRO"
                 })
            Assert.DoesNotContain(forbidden, xml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Учётная_запись_локальная_администратор_без_пароля()
    {
        var account = Parse(WindowsAnswerFileBuilder.Build(Options("Мария"))).Descendants(Ns + "LocalAccount").Single();

        Assert.Equal("add", account.Attribute(Wcm + "action")!.Value);
        Assert.Equal("Мария", account.Element(Ns + "Name")!.Value);
        Assert.Equal("Administrators", account.Element(Ns + "Group")!.Value);
        Assert.Equal("", account.Element(Ns + "Password")!.Element(Ns + "Value")!.Value);
        Assert.Equal("true", account.Element(Ns + "Password")!.Element(Ns + "PlainText")!.Value);
    }

    [Fact]
    public void Экраны_первой_настройки_скрыты_а_отправка_данных_не_включается()
    {
        var oobe = Parse(WindowsAnswerFileBuilder.Build(Options())).Descendants(Ns + "OOBE").Single();

        Assert.Equal("true", oobe.Element(Ns + "HideOnlineAccountScreens")!.Value);
        Assert.Equal("true", oobe.Element(Ns + "HideEULAPage")!.Value);
        Assert.Equal("true", oobe.Element(Ns + "HideWirelessSetupInOOBE")!.Value);
        Assert.Equal("3", oobe.Element(Ns + "ProtectYourPC")!.Value);
    }

    [Fact]
    public void Язык_раскладки_и_часовой_пояс_берутся_из_настроек()
    {
        var doc = Parse(WindowsAnswerFileBuilder.Build(Options()));

        Assert.Equal("ru-RU;en-US", doc.Descendants(Ns + "InputLocale").Single().Value);
        Assert.Equal("ru-RU", doc.Descendants(Ns + "UserLocale").Single().Value);
        Assert.Equal("ru-RU", doc.Descendants(Ns + "SystemLocale").Single().Value);
        Assert.Equal("Russian Standard Time", doc.Descendants(Ns + "TimeZone").Single().Value);
        // Язык интерфейса не задаётся: его определяет сам образ, а несовпадение останавливает установку.
        Assert.Empty(doc.Descendants(Ns + "UILanguage"));
    }

    [Theory]
    [InlineData("amd64")]
    [InlineData("arm64")]
    public void Архитектура_проставляется_во_всех_компонентах(string architecture)
    {
        var components = Parse(WindowsAnswerFileBuilder.Build(Options(architecture: architecture)))
            .Descendants(Ns + "component").ToList();

        Assert.Equal(2, components.Count);
        Assert.All(components, c => Assert.Equal(architecture, c.Attribute("processorArchitecture")!.Value));
        Assert.All(components, c => Assert.Equal("31bf3856ad364e35", c.Attribute("publicKeyToken")!.Value));
    }

    [Fact]
    public void Команда_первого_входа_ищет_набор_на_всех_дисках_и_запускает_restore_cmd()
    {
        var command = Parse(WindowsAnswerFileBuilder.Build(Options(kitPath: "Мой набор\\Kit")))
            .Descendants(Ns + "SynchronousCommand").Single();

        Assert.Equal("1", command.Element(Ns + "Order")!.Value);
        string line = command.Element(Ns + "CommandLine")!.Value;
        Assert.Equal(
            "cmd.exe /c \"for %d in (D E F G H I J K L M N O P Q R S T U V W X Y Z C) do " +
            "if exist \"%d:\\Мой набор\\Kit\\restore.cmd\" start \"\" \"%d:\\Мой набор\\Kit\\restore.cmd\"\"",
            line);
        // Ограничение Windows на длину команды первого входа.
        Assert.True(line.Length < 1024);
    }

    [Fact]
    public void Набор_в_корне_флешки_даёт_путь_без_папки()
    {
        Assert.Contains("\"%d:\\restore.cmd\"", WindowsAnswerFileBuilder.BuildFirstLogonCommand(""));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("имя/с/косой")]
    [InlineData("a\"b")]
    [InlineData("user@domain")]
    [InlineData("слишком_длинное_имя_учётной_записи")]
    [InlineData("Administrator")]
    [InlineData("администратор")]
    [InlineData("точка.")]
    public void Недопустимое_имя_учётной_записи_отклоняется(string name)
    {
        Assert.NotNull(WindowsAnswerFileBuilder.ValidateAccountName(name));
        Assert.Throws<ArgumentException>(() => WindowsAnswerFileBuilder.Build(Options(account: name)));
    }

    [Theory]
    [InlineData("Иван")]
    [InlineData("Vench")]
    [InlineData("Иван Петров")]
    [InlineData("user-1_test")]
    public void Обычное_имя_учётной_записи_принимается(string name)
    {
        Assert.Null(WindowsAnswerFileBuilder.ValidateAccountName(name));
    }

    [Theory]
    [InlineData("C:\\Kit")]
    [InlineData("..\\Kit")]
    [InlineData("Kit\\..\\..\\Windows")]
    [InlineData("Kit\" & del /q C:\\*")]
    [InlineData("Kit%TEMP%")]
    [InlineData("Kit&calc")]
    [InlineData("Kit(1)")]
    public void Путь_к_набору_способный_изменить_команду_отклоняется(string kitPath)
    {
        Assert.Throws<ArgumentException>(() => WindowsAnswerFileBuilder.Build(Options(kitPath: kitPath)));
    }

    [Fact]
    public void Имя_с_символами_разметки_экранируется_а_не_ломает_XML()
    {
        // Амперсанд в имени учётной записи допустим — в XML он обязан уйти как сущность.
        string xml = WindowsAnswerFileBuilder.Build(Options(account: "Tom & Jerry"));

        Assert.Contains("Tom &amp; Jerry", xml);
        Assert.Equal("Tom & Jerry", Parse(xml).Descendants(Ns + "Name").Single().Value);
    }

    [Fact]
    public void Набор_на_системном_диске_отклоняется_и_файл_в_его_корень_не_пишется()
    {
        // Запись в корень настоящего диска в юнит-тесте не проверяется: корень тестового
        // каталога — рабочий диск машины. Здесь он объявлен системным, и запись обязана
        // быть отклонена до любых обращений к диску.
        string kit = Path.Combine(_dir, "Kit");
        Directory.CreateDirectory(kit);
        string driveRoot = Path.GetPathRoot(Path.GetFullPath(kit))!;
        string target = Path.Combine(driveRoot, WindowsAnswerFileBuilder.FileName);
        bool existedBefore = File.Exists(target);

        var error = Assert.Throws<ArgumentException>(() =>
            WindowsAnswerFileBuilder.WriteToDriveRoot(kit, path => Options(kitPath: path), systemDriveRoot: driveRoot));

        Assert.Contains("на флешке", error.Message);
        Assert.Equal(existedBefore, File.Exists(target));
    }

    [Fact]
    public void Сетевая_папка_отклоняется()
    {
        Assert.Throws<ArgumentException>(() =>
            WindowsAnswerFileBuilder.WriteToDriveRoot(@"\\server\share\Kit", path => Options(kitPath: path)));
    }
}
