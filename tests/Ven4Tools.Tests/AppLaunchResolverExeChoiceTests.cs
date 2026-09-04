using Ven4Tools.Services;

namespace Ven4Tools.Tests;

// Выбор основного exe в папке установки. Кнопка «▶ Запустить» запускает найденный
// файл в клиенте, работающем с правами администратора, поэтому «взял не тот exe» —
// это не косметика.
//
// Проверяемая здесь ошибка: сопоставление по имени шло через
// normalizedName.Contains(Normalize(имя_файла)), а Normalize оставляет только буквы
// и цифры и возвращает "" для имени вроде «-.exe». string.Contains("") истинно
// ВСЕГДА, поэтому такой файл выигрывал сопоставление у настоящего кандидата и
// становился целью запуска.
public sealed class AppLaunchResolverExeChoiceTests : IDisposable
{
    private readonly string _dir;

    public AppLaunchResolverExeChoiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "Ven4ToolsExeChoice_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Touch(string fileName, int sizeBytes = 16)
    {
        string path = Path.Combine(_dir, fileName);
        File.WriteAllBytes(path, new byte[sizeBytes]);
        return path;
    }

    [Fact]
    public void ФайлБезБуквВИмениНеВыигрываетСопоставление()
    {
        // «-.exe» нормализуется в пустую строку — до фикса именно он и возвращался.
        Touch("-.exe");
        string expected = Touch("MyApp.exe");

        string? chosen = AppLaunchResolver.FindBestExeInDirectory(_dir, "MyApp");

        Assert.Equal(expected, chosen);
    }

    [Fact]
    public void ВыбираетСовпадающийПоИмениАНеСамыйКрупный()
    {
        // Совпадение по названию продукта важнее размера файла.
        Touch("SomeOtherTool.exe", sizeBytes: 4096);
        string expected = Touch("MyApp.exe", sizeBytes: 16);

        string? chosen = AppLaunchResolver.FindBestExeInDirectory(_dir, "MyApp");

        Assert.Equal(expected, chosen);
    }

    [Fact]
    public void БезСовпаденияПоИмениБерётКрупнейший()
    {
        Touch("aaa.exe", sizeBytes: 16);
        string expected = Touch("bbb.exe", sizeBytes: 8192);

        string? chosen = AppLaunchResolver.FindBestExeInDirectory(_dir, "Совершенно другое название");

        Assert.Equal(expected, chosen);
    }

    [Fact]
    public void СлужебныеУстановщикиИсключаются()
    {
        // uninstall/setup/update и т.п. не должны становиться целью запуска.
        Touch("unins000.exe", sizeBytes: 65536);
        Touch("setup.exe", sizeBytes: 65536);
        string expected = Touch("MyApp.exe", sizeBytes: 16);

        string? chosen = AppLaunchResolver.FindBestExeInDirectory(_dir, "MyApp");

        Assert.Equal(expected, chosen);
    }

    [Fact]
    public void ПустаяПапкаДаётNull()
    {
        Assert.Null(AppLaunchResolver.FindBestExeInDirectory(_dir, "MyApp"));
    }
}
