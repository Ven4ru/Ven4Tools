using System;
using System.Linq;
using Ven4Tools.Services;

namespace Ven4Tools.Tests;

/// <summary>
/// Разбор вывода `winget list` кэшируется в словарь один раз (LoadFromOutput), а не
/// пересчитывается на каждый вызов IsInstalled/GetInstalledVersion. Тесты закрывают
/// именно наблюдаемое поведение: результат обеих проверок обязан совпадать с прежним
/// «разобрать заново на каждый вызов», включая пустой и нераспознаваемый вывод.
/// </summary>
public sealed class InstalledAppsServiceTests
{
    // Реалистичный вывод `winget list`: заголовок, разделитель, строки с колонкой
    // Available (версия НЕ является «следующим словом» после Id), строка без версии
    // короче колонки Version и футер-суммарник без выравнивания колонок.
    private const string SampleOutput =
        "Name                 Id                      Version      Available    Source\n" +
        "-----------------------------------------------------------------------------\n" +
        "7-Zip 23.01 (x64)    7zip.7zip               23.01        24.09        winget\n" +
        "Notepad++ (64-bit)   Notepad++.Notepad++     8.6.9                     winget\n" +
        "Git                  Git.Git                 2.45.1       2.46.0       winget\n" +
        "Старое приложение    Vendor.Legacy\n" +
        "Доступны обновления: 2.\n";

    // Русский заголовок winget: колонка называется «Версия», позиция колонки другая.
    private const string RussianHeaderOutput =
        "Имя        ИД             Версия     Доступно   Источник\n" +
        "---------------------------------------------------------\n" +
        "7-Zip      7zip.7zip      23.01      24.09      winget\n";

    private static InstalledAppsService Loaded(string output)
    {
        var service = new InstalledAppsService();
        service.LoadFromOutput(output);
        return service;
    }

    // ── Эталон: прежняя реализация, разбиравшая сырой вывод на КАЖДЫЙ вызов ──────
    // Держим её здесь целиком, чтобы «не разошлось с однократным разбором» было
    // проверяемым утверждением, а не обещанием: оба варианта прогоняются на одном и
    // том же выводе и обязаны дать одинаковый ответ на каждом идентификаторе.

    private static bool LegacyIsInstalled(string raw, string wingetId)
    {
        if (string.IsNullOrEmpty(wingetId) || string.IsNullOrEmpty(raw)) return false;
        return LegacyContainsToken(raw, wingetId);
    }

    private static bool LegacyContainsToken(string haystack, string token)
    {
        int idx = 0;
        while ((idx = haystack.IndexOf(token, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            bool leftBoundary = idx == 0 || char.IsWhiteSpace(haystack[idx - 1]);
            int end = idx + token.Length;
            bool rightBoundary = end >= haystack.Length || char.IsWhiteSpace(haystack[end]);
            if (leftBoundary && rightBoundary) return true;
            idx++;
        }
        return false;
    }

    private static string LegacyGetInstalledVersion(string raw, string wingetId)
    {
        if (string.IsNullOrEmpty(wingetId) || string.IsNullOrEmpty(raw)) return string.Empty;

        var lines = raw.Replace("\r\n", "\n").Split('\n');
        int sepIndex = Array.FindIndex(lines, IsSeparator);
        if (sepIndex <= 0) return string.Empty;

        int versionColumn = LegacyFindVersionColumn(lines[sepIndex - 1]);
        if (versionColumn < 0) return string.Empty;

        for (int i = sepIndex + 1; i < lines.Length; i++)
        {
            string line = lines[i];
            if (!LegacyContainsToken(line, wingetId)) continue;
            if (line.Length <= versionColumn) return string.Empty;

            string rest = line.Substring(versionColumn).TrimStart();
            int space = rest.IndexOfAny(new[] { ' ', '\t' });
            return space < 0 ? rest : rest.Substring(0, space);
        }
        return string.Empty;
    }

    // Копия критерия строки-разделителя (Ven4Tools.Shared.WingetOutputParser внутренний
    // для сборки клиента, но его обёртка WingetRunner.IsTableSeparator видна тестам —
    // здесь всё же держим независимую копию, чтобы эталон не зависел от кода под тестом).
    private static bool IsSeparator(string line)
    {
        string t = line.Trim();
        return t.Length > 0 && t.Contains('-') && t.All(c => c == '-' || c == ' ');
    }

    private static int LegacyFindVersionColumn(string header)
    {
        foreach (var key in new[] { "Version", "Версия" })
        {
            int idx = header.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0) return idx;
        }
        return -1;
    }

    // ── (а) Кэшированный разбор не расходится с разбором на каждый вызов ─────────

    public static TheoryData<string> Outputs => new()
    {
        SampleOutput,
        SampleOutput.Replace("\n", "\r\n"),
        RussianHeaderOutput,
        string.Empty,
        // Вывод без таблицы (winget не нашёл пакет / ошибка источника): разделителя
        // нет, колонку Version определить не из чего.
        "No installed package found matching input criteria.\n",
        // Таблица есть, а колонки Version в заголовке нет — версия неизвестна,
        // но факт присутствия ID из вывода по-прежнему читается.
        "Name       Id\n-----------------------\n7-Zip      7zip.7zip\n"
    };

    [Theory]
    [MemberData(nameof(Outputs))]
    public void CachedParsing_MatchesPerCallParsing_ForEveryToken(string output)
    {
        var service = Loaded(output);

        // Проверяем не только «свои» ID, но и заведомо посторонние токены вывода
        // (названия колонок, слова футера, куски названий) — там, где однократный
        // разбор мог бы разойтись с прежним посимвольным поиском.
        var ids = output
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Concat(new[] { "7zip.7zip", "7ZIP.7ZIP", "Git.Git", "Missing.Package", "zip", "Version" })
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        foreach (var id in ids)
        {
            Assert.Equal(LegacyIsInstalled(output, id), service.IsInstalled(id));
            Assert.Equal(LegacyGetInstalledVersion(output, id), service.GetInstalledVersion(id));
        }
    }

    [Fact]
    public void RepeatedCalls_ReturnSameResult()
    {
        var service = Loaded(SampleOutput);

        for (int i = 0; i < 3; i++)
        {
            Assert.True(service.IsInstalled("7zip.7zip"));
            Assert.Equal("23.01", service.GetInstalledVersion("7zip.7zip"));
            Assert.False(service.IsInstalled("Missing.Package"));
            Assert.Equal(string.Empty, service.GetInstalledVersion("Missing.Package"));
        }
    }

    [Fact]
    public void VersionTakenFromVersionColumn_NotNextWordAfterId()
    {
        // Колонки выровнены пробелами: «следующее слово» после Id у 7zip — это его
        // версия, а вот у строки без версии им оказался бы Source. Берём строго по
        // позиции колонки Version.
        var service = Loaded(SampleOutput);

        Assert.Equal("23.01", service.GetInstalledVersion("7zip.7zip"));
        Assert.Equal("8.6.9", service.GetInstalledVersion("Notepad++.Notepad++"));
        Assert.Equal("2.45.1", service.GetInstalledVersion("Git.Git"));
    }

    [Fact]
    public void RowShorterThanVersionColumn_HasNoVersion_ButIsStillInstalled()
    {
        var service = Loaded(SampleOutput);

        Assert.True(service.IsInstalled("Vendor.Legacy"));
        Assert.Equal(string.Empty, service.GetInstalledVersion("Vendor.Legacy"));
    }

    [Fact]
    public void IdMatching_IsCaseInsensitive()
    {
        var service = Loaded(SampleOutput);

        Assert.True(service.IsInstalled("7ZIP.7ZIP"));
        Assert.Equal("23.01", service.GetInstalledVersion("7zIp.7ZiP"));
    }

    [Fact]
    public void PartialToken_IsNotAMatch()
    {
        // «zip» — часть Id, но не отдельный токен: границы по пробелам обязательны.
        var service = Loaded(SampleOutput);

        Assert.False(service.IsInstalled("zip"));
        Assert.False(service.IsInstalled("7zip"));
    }

    [Fact]
    public void EmptyOutput_And_EmptyId_AreHandled()
    {
        var empty = Loaded(string.Empty);

        Assert.False(empty.IsInstalled("7zip.7zip"));
        Assert.Equal(string.Empty, empty.GetInstalledVersion("7zip.7zip"));
        Assert.False(empty.IsInstalled(""));
        Assert.Equal(string.Empty, empty.GetInstalledVersion(""));

        var loaded = Loaded(SampleOutput);
        Assert.False(loaded.IsInstalled(""));
        Assert.Equal(string.Empty, loaded.GetInstalledVersion(""));
    }

    [Fact]
    public void NewService_WithoutRefresh_ReportsNothingInstalled()
    {
        // Контракт «до RefreshAsync ничего не известно» сохранён (раньше это
        // обеспечивала проверка пустого _rawOutput).
        var service = new InstalledAppsService();

        Assert.False(service.IsInstalled("7zip.7zip"));
        Assert.Equal(string.Empty, service.GetInstalledVersion("7zip.7zip"));
    }

    [Fact]
    public void RussianHeader_VersionColumnRecognised()
    {
        var service = Loaded(RussianHeaderOutput);

        Assert.True(service.IsInstalled("7zip.7zip"));
        Assert.Equal("23.01", service.GetInstalledVersion("7zip.7zip"));
    }

    // ── (б) Верификация установки запрашивает ОДИН пакет, а не весь список ───────

    /// <summary>
    /// Что здесь реально проверено: строка аргументов, с которой
    /// InstalledAppsService.QuerySinglePackageAsync запускает winget при верификации
    /// установки — это `list` с фильтром по конкретному ID и `--exact`, а НЕ голый
    /// `list` (WingetArgs.Query("list")), которым снимается полный список.
    ///
    /// Что юнит-тестом здесь не проверяется и почему: сам факт запуска процесса.
    /// WingetRunner стартует winget.exe напрямую через Process.Start по доверенному
    /// пути (TrustedExecutablePaths.ResolveWinget), без точки подмены —
    /// заглушку процесса подставить некуда, а реальный запуск winget в юнит-тестах
    /// зависел бы от состава установленного ПО на машине и занимал бы секунды.
    /// Поэтому проверяется единственное, что определяет объём работы winget, —
    /// аргументы; то, что верификация ходит именно этим методом, закрыто тем, что
    /// InstallationService.Outcome больше не создаёт InstalledAppsService и не
    /// вызывает RefreshAsync (см. соседний тест).
    /// </summary>
    [Fact]
    public void SinglePackageArgs_QueryOnePackage_NotWholeList()
    {
        var args = InstalledAppsService.SinglePackageArgs("7zip.7zip");

        Assert.Equal("list", args[0]);
        Assert.Contains("--id", args);
        Assert.Contains("7zip.7zip", args);
        Assert.Contains("--exact", args);

        // Обязательные для любого неинтерактивного вызова winget флаги на месте.
        Assert.Contains("--accept-source-agreements", args);
        Assert.Contains("--disable-interactivity", args);

        // Полная выгрузка (`winget list` без фильтра) — это ровно WingetArgs.Query("list").
        Assert.NotEqual(WingetArgs.Query("list"), args);

        // --source winget здесь недопустим: он отсекает записи ARP, а установку через
        // choco/прямую ссылку/локальный установщик видно только по ним.
        Assert.DoesNotContain("--source", args);
    }

    /// <summary>
    /// Верификация результата установки больше не поднимает полный список
    /// установленного. Проверяется по исходнику InstallationService.Outcome.cs:
    /// метод пути верификации не создаёт InstalledAppsService и не зовёт RefreshAsync.
    /// Рефлексией это не видно (обращения к типу не остаются в метаданных метода),
    /// а живой запуск установки в юнит-тесте невозможен — см. пояснение выше.
    /// </summary>
    [Fact]
    public void InstallationOutcome_DoesNotRefreshWholeInstalledList()
    {
        string source = ReadRepoFile("Ven4Tools", "Services", "InstallationService.Outcome.cs");

        Assert.DoesNotContain("new InstalledAppsService()", source);
        Assert.DoesNotContain("RefreshAsync()", source);
        Assert.Contains("QuerySinglePackageAsync", source);
    }

    // Путь к файлу репозитория от каталога сборки тестов
    // (tests/Ven4Tools.Tests/bin/<Config>/<TFM>) — вверх до корня репозитория.
    private static string ReadRepoFile(params string[] relative)
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "Ven4Tools.sln")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        string path = System.IO.Path.Combine(new[] { dir!.FullName }.Concat(relative).ToArray());
        Assert.True(System.IO.File.Exists(path), $"Не найден файл {path}");
        return System.IO.File.ReadAllText(path);
    }
}
