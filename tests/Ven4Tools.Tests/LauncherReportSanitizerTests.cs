using Ven4Tools.Launcher.Helpers;

namespace Ven4Tools.Tests;

/// <summary>
/// Очистка персональных данных перед отправкой отчётов ПУБЛИЧНЫМ issue на GitHub.
///
/// Окна «Отчёт об ошибке» и «Ошибки установки» — единственные места лаунчера, откуда
/// данные пользователя уходят наружу. Живой прогон 2026-09-10 показал, что оба окна
/// отображают их как есть (это правильно — пользователь смотрит на своё), а очистка
/// происходит уже в GitHubService.CreateIssueAsync. Кнопки отправки при прогоне
/// намеренно НЕ нажимались: их нажатие создаёт настоящий публичный issue. Проверка
/// того, что уходит наружу, живёт здесь.
///
/// Тексты собираются из Environment.UserName/MachineName текущей машины — тест не
/// зависит от того, у кого он запущен.
/// </summary>
public sealed class LauncherReportSanitizerTests
{
    private static string User => Environment.UserName;
    private static string Machine => Environment.MachineName;

    [Fact]
    public void Sanitize_RemovesProfilePathFromCrashBody()
    {
        string body =
            $"Не найден файл C:\\Users\\{User}\\Documents\\Ven4Tools_Client\\Data\\master.json\n" +
            $"   at Ven4Tools.Services.CatalogLoaderService.LoadAsync() in C:\\Users\\{User}\\src\\Ven4Tools.cs:line 42";

        string clean = PersonalDataSanitizer.Sanitize(body);

        Assert.DoesNotContain(User, clean, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<пользователь>", clean);
        // Остальное для диагностики должно уцелеть — иначе отчёт бесполезен.
        Assert.Contains("master.json", clean);
        Assert.Contains("line 42", clean);
    }

    [Fact]
    public void Sanitize_RemovesMachineName()
    {
        string clean = PersonalDataSanitizer.Sanitize($"   at {Machine}.Boot()");

        Assert.DoesNotContain(Machine, clean, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<машина>", clean);
    }

    /// <summary>
    /// Журнал неудачных установок — самая недооценённая утечка: у приложения,
    /// добавленного вручную, и название, и идентификатор берутся из имени файла
    /// установщика, а он лежит в профиле пользователя и запросто содержит его имя.
    /// Данные из живого прогона 2026-09-10.
    /// </summary>
    [Fact]
    public void Sanitize_CleansManuallyAddedAppEntry()
    {
        string body =
            $"📦 Приложение: Установщик {User} setup.exe\n" +
            $"🆔 ID: local.{User}.setup\n" +
            $"❌ Ошибка: Отказано в доступе: C:\\Users\\{User}\\Downloads\\setup.exe";

        string clean = PersonalDataSanitizer.Sanitize(body);

        Assert.DoesNotContain(User, clean, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Отказано в доступе", clean);
        Assert.Contains("setup.exe", clean);
    }

    [Theory]
    [InlineData("C:/Users/{0}/Downloads/x.exe")]
    [InlineData("\\\\SERVER\\Users\\{0}\\share\\x.exe")]
    [InlineData("D:\\Users\\{0}\\portable\\x.exe")]
    public void Sanitize_HandlesAlternatePathShapes(string template)
    {
        string clean = PersonalDataSanitizer.Sanitize(string.Format(template, User));

        Assert.DoesNotContain(User, clean, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HashSessionId_DoesNotExposeOriginal()
    {
        const string session = "11111111-2222-3333-4444-555555555555";

        string hash = PersonalDataSanitizer.HashSessionId(session);

        Assert.Equal(8, hash.Length);
        Assert.DoesNotContain(session, hash);
        // Дедупликация отчётов держится на стабильности хэша.
        Assert.Equal(hash, PersonalDataSanitizer.HashSessionId(session));
        Assert.NotEqual(hash, PersonalDataSanitizer.HashSessionId(session + "x"));
    }

    [Fact]
    public void Sanitize_HandlesNullAndEmpty()
    {
        Assert.Equal("", PersonalDataSanitizer.Sanitize(null));
        Assert.Equal("", PersonalDataSanitizer.Sanitize(""));
    }
}
