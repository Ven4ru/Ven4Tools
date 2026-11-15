using Ven4Tools.Services;
using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Tests;

/// <summary>
/// Единый откат «Персонализации» (деинсталлятор и «Удалить клиент»): все шаги идут по порядку,
/// сбой одного не останавливает остальные, код возврата честный. Настоящая система не трогается.
/// </summary>
public sealed class PersonalizationRevertTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "v4t-revert-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void AllSteps_RunInOrder_ExitCodeZero()
    {
        var calls = new List<string>();
        var revert = new PersonalizationRevert(new[]
        {
            new PersonalizationRevert.Step("a", () => { calls.Add("a"); return true; }),
            new PersonalizationRevert.Step("b", () => { calls.Add("b"); return true; }),
        });
        var r = revert.RevertAll();
        Assert.Equal(new[] { "a", "b" }, calls);
        Assert.Equal(0, r.ExitCode);
        Assert.Equal(new[] { "a", "b" }, r.Reverted);
    }

    [Fact]
    public void FailingStep_DoesNotStopOthers_ExitCodeOne()
    {
        var calls = new List<string>();
        var revert = new PersonalizationRevert(new[]
        {
            new PersonalizationRevert.Step("падает", () => { calls.Add("1"); throw new IOException("тест: файл занят"); }),
            new PersonalizationRevert.Step("не до конца", () => { calls.Add("2"); return false; }),
            new PersonalizationRevert.Step("ок", () => { calls.Add("3"); return true; }),
        });
        var r = revert.RevertAll();
        Assert.Equal(new[] { "1", "2", "3" }, calls);
        Assert.Equal(1, r.ExitCode);
        Assert.Equal(new[] { "падает", "не до конца" }, r.Failed);
        Assert.Equal(new[] { "ок" }, r.Reverted);
    }

    [Fact]
    public void DefaultSteps_CoverEverySection_StartMenuFirst()
    {
        var names = PersonalizationRevert.DefaultSteps().Select(s => s.Name).ToList();
        Assert.Equal("Свой Пуск", names[0]);
        Assert.Contains("Тема панели задач и ушки Венчика", names);
        Assert.Contains("Панель задач и Пуск", names);
        foreach (var part in Enum.GetValues<PersonalizationPart>())
            Assert.Contains("Персонализация: " + part, names);
        // Тема — раньше настроек панели: её «как было» снято раньше (так же откатывает вкладка).
        Assert.True(names.IndexOf("Тема панели задач и ушки Венчика") < names.IndexOf("Панель задач и Пуск"));
    }

    [Fact]
    public void Launcher_ReadsClientPathFromSettings()
    {
        Directory.CreateDirectory(_dir);
        var settings = Path.Combine(_dir, "launcher_settings.json");
        File.WriteAllText(settings, "{\"MinimizeToTray\":true,\"ClientPath\":\"D:\\\\Tools\\\\Ven4Tools_Client\"}");
        Assert.Equal(@"D:\Tools\Ven4Tools_Client", PersonalizationRevertRunner.ReadClientPath(settings));
        Assert.Null(PersonalizationRevertRunner.ReadClientPath(Path.Combine(_dir, "нет.json")));
    }

    [Fact]
    public void Launcher_NoClient_ReturnsTwo_WithoutStartingAnything()
    {
        Assert.Equal(2, PersonalizationRevertRunner.RunFor(Path.Combine(_dir, "нет-клиента")));
        Assert.Equal(2, PersonalizationRevertRunner.RunFor(null));
    }

    [Fact]
    public void ClientAndLauncher_UseTheSameSwitch()
    {
        Assert.Equal(PersonalizationRevert.Switch, PersonalizationRevertRunner.Switch);
    }
}
