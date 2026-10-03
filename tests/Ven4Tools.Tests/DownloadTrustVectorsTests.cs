using System.Text.Json;
using LauncherDownloadValidator = Ven4Tools.Launcher.Services.DownloadValidator;

namespace Ven4Tools.Tests;

/// <summary>
/// Правила доверия к источнику загрузки живут в двух местах: в лаунчере
/// (DownloadValidator) и в install.ps1 (Test-AllowedSource), который ставит сам
/// лаунчер. Скрипт не может вызвать код лаунчера, поэтому правило в нём повторено —
/// и уже дважды переносилось вручную с неполным совпадением.
///
/// Общий набор примеров Fixtures/download-trust-vectors.json проверяется с двух
/// сторон: здесь — против лаунчера, в tests/scripts/Test-InstallScriptTrustRules.ps1 —
/// против install.ps1. Правка правила в одном месте без правки набора роняет сборку.
/// </summary>
public sealed class DownloadTrustVectorsTests
{
    private sealed record Vector(string Url, bool Launcher, bool InstallScript);

    private static IReadOnlyList<Vector> Load()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "download-trust-vectors.json");
        var vectors = JsonSerializer.Deserialize<List<Vector>>(
            File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(vectors);
        Assert.NotEmpty(vectors!);
        return vectors!;
    }

    [Fact]
    public void Лаунчер_ОтвечаетНаКаждыйПримерКакЗаписаноВНаборе()
    {
        var wrong = Load()
            .Where(v => LauncherDownloadValidator.IsAllowedDownloadHost(v.Url) != v.Launcher)
            .Select(v => $"{v.Url} — ожидалось {v.Launcher}")
            .ToList();

        Assert.True(wrong.Count == 0, "Расхождение с набором примеров:\n" + string.Join("\n", wrong));
    }

    [Fact]
    public void УстановочныйСкрипт_НеДоверяетТомуЧемуНеДоверяетЛаунчер()
    {
        // install.ps1 ставит только сам лаунчер, поэтому его список — подмножество
        // лаунчерного (без хостов Microsoft). Обратное было бы дырой: скрипт запустил
        // бы файл с хоста, которому лаунчер не верит.
        var wider = Load().Where(v => v.InstallScript && !v.Launcher).Select(v => v.Url).ToList();

        Assert.True(wider.Count == 0, "Скрипту разрешено больше, чем лаунчеру:\n" + string.Join("\n", wider));
    }

    [Fact]
    public void Набор_ПокрываетОбаИсхода()
    {
        var vectors = Load();

        Assert.Contains(vectors, v => v.Launcher && v.InstallScript);
        Assert.Contains(vectors, v => !v.Launcher && !v.InstallScript);
        Assert.Contains(vectors, v => v.Launcher && !v.InstallScript);
    }
}
