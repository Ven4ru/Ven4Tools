# Установка клиента из локального файла (лаунчер) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Дать лаунчеру устанавливать клиента из уже лежащего на диске zip-архива — с той же fail-closed гарантией целостности, что и у сетевой загрузки, но новым, независимым механизмом (офлайн-подпись внутри архива + сетевой allow-list для архивных версий, выпущенных до этой фичи).

**Architecture:** Новая утилита `Tools/ClientArchiveSigner` встраивает подпись `_ven4tools_signature.json` в zip на релизе (домен-сепаратор + канонический хеш содержимого архива, ECDSA P-256, по образцу `UpdateManifestSigner`). Новый лаунчер-сервис `LocalArchiveVerifier` при установке проверяет: сначала встроенную подпись офлайн; если её нет — обязательно по сети сверяет whole-file SHA256 с новым списком `historicalClientArchives` в уже подписанном `version.json` (архивные версии, выпущенные до этой фичи). В обоих случаях — best-effort сверка с `revokedClientHashes`. UI-кнопка «Установить из файла...» и CLI `--install-from=<path> [--silent]` — оба ведут в общий с сетевой загрузкой путь распаковки/установки (`SafeZipExtractor` + `TransactionalDirectoryInstaller`), чтобы не плодить два параллельных пути.

**Tech Stack:** .NET 8, WPF (лаунчер), `System.IO.Compression.ZipArchive`, `System.Security.Cryptography.ECDsa` (P-256/SHA-256), xUnit (`tests/Ven4Tools.Tests`), FlaUI/UIA3 (`tests/Ven4Tools.UITests`).

## Global Constraints

- Спека: `docs/superpowers/specs/2026-08-02-local-client-archive-install-design.md` (читать целиком перед началом — этот план реализует её, включая позднейшую правку про `historicalClientArchives`).
- Общей библиотеки между `Ven4Tools.Launcher` и `Tools/*`-утилитами в проекте нет намеренно — канонический алгоритм хеширования продублирован в обоих местах и должен оставаться байт-в-байт идентичным при любых будущих правках.
- Домен-сепаратор для этого типа артефакта: `"Ven4Tools.ClientArchive.v1\n"` — отдельный от `UpdateManifestSigner`/`NotificationsSigner`/`CatalogSigner`, свой отдельный ключ (не переиспользовать чужие).
- **Отклонение от исходного текста спеки (осознанное, см. обоснование в Task 3):** подписываемый payload — `DomainSeparator + version + "\n" + H_canonical`, а не только `DomainSeparator + H_canonical` — иначе поле `version` в `_ven4tools_signature.json` можно подменить без приватного ключа, не инвалидируя подпись.
- Приватный ключ — НЕ коммитить в репозиторий. Хранится на машине разработчика вне репо, по уже установленному в проекте паттерну (`$env:USERPROFILE\.ven4tools\<name>-signing-private.pem`, см. `Tools/deploy-version-manifest.ps1`, `Tools/sign-notifications.ps1`).
- 0 ошибок, 0 предупреждений при `dotnet build` перед каждым коммитом (pre-commit hook уже это проверяет).
- Тексты (сообщения, логи, комментарии) — только на русском.
- Не упоминать вспомогательные инструменты разработки нигде в репозитории.

---

## File Structure

| Файл | Назначение |
|---|---|
| `Tools/ClientArchiveSigner/ClientArchiveSigner.csproj` | Новый standalone-проект (как `CatalogSigner`), не в `Ven4Tools.sln` |
| `Tools/ClientArchiveSigner/Program.cs` | CLI: подпись архива при релизе + `verify`-режим |
| `Tools/sign-client-archive.ps1` | Обёртка-скрипт (по образцу `sign-notifications.ps1`) — собирает утилиту при необходимости, подписывает, локально проверяет |
| `Ven4Tools.Launcher/Services/CanonicalArchiveHasher.cs` | Канонический хеш содержимого zip (используется и верификатором в лаунчере, и (продублированно) утилитой подписи) |
| `Ven4Tools.Launcher/Services/ClientArchiveVerifier.cs` | Обёртка над `EcdsaManifestVerifier` со своим встроенным публичным ключом и доменом |
| `Ven4Tools.Launcher/Models/ClientArchiveSignatureFile.cs` | POCO для `_ven4tools_signature.json` |
| `Ven4Tools.Launcher/Models/CdnVersionInfo.cs` | **Изменить** — добавить `RevokedClientHashes`, `HistoricalClientArchives` |
| `Ven4Tools.Launcher/Services/LocalArchiveVerifier.cs` | Оркестрация: офлайн-подпись → (если нет) исторический список по сети → отзыв |
| `Ven4Tools.Launcher/MainWindow.Download.cs` | **Изменить** — вынести общий хвост «распаковка+установка» в переиспользуемый метод |
| `Ven4Tools.Launcher/MainWindow.Download.LocalArchive.cs` | Новый — обработчик кнопки + `InstallFromLocalArchiveAsync` |
| `Ven4Tools.Launcher/MainWindow.xaml` | **Изменить** — кнопка «Установить из файла...» |
| `Ven4Tools.Launcher/CliInstallRunner.cs` | Новый — headless-путь для `--install-from` |
| `Ven4Tools.Launcher/App.xaml.cs` | **Изменить** — разбор `--install-from=`/`--silent` в `OnStartup` |
| `Ven4Tools.Launcher/Services/LauncherPaths.cs` | **Изменить** — добавить `ResolveClientPath()`, общий для `MainWindow` и `CliInstallRunner` |
| `version.json`, `_release/version.json` | ~~Изменить~~ — **не трогаются** (untracked/gitignored, не часть репозитория, см. правку в Task 2, Step 5) |
| `tests/Ven4Tools.Tests/CanonicalArchiveHasherTests.cs` | Новый |
| `tests/Ven4Tools.Tests/CdnVersionInfoDeserializationTests.cs` | Новый |
| `tests/Ven4Tools.Tests/LocalArchiveVerifierTests.cs` | Новый |
| `tests/Ven4Tools.Tests/Fixtures/client-archive-signed-sample.zip` | Новый — фикстура, подписанная реальным продакшн-ключом (не секрет, только публичная сторона проверяема) |
| `tests/Ven4Tools.Tests/Fixtures/client-archive-unsigned-sample.zip` | Новый — тот же контент, без записи подписи |
| `tests/Ven4Tools.UITests/LauncherSmokeTests.cs` | **Изменить** — добавить `btnInstallFromFile` в существующие проверки |
| `tests/Ven4Tools.UITests/Snapshots/launcher-main.png` | **Изменить** — перегенерировать после добавления кнопки |

---

### Task 1: `CanonicalArchiveHasher` — канонический хеш содержимого архива

**Files:**
- Create: `Ven4Tools.Launcher/Services/CanonicalArchiveHasher.cs`
- Test: `tests/Ven4Tools.Tests/CanonicalArchiveHasherTests.cs`

**Interfaces:**
- Produces: `internal static class CanonicalArchiveHasher { internal const string SignatureEntryName = "_ven4tools_signature.json"; public static string ComputeHex(ZipArchive archive); }` — используется `LocalArchiveVerifier` (Task 5) и продублирован в `Tools/ClientArchiveSigner` (Task 3).

- [ ] **Step 1: Написать падающий тест**

```csharp
using System.IO.Compression;
using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Tests;

public sealed class CanonicalArchiveHasherTests
{
    private static MemoryStream BuildZip(Action<ZipArchive> populate)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            populate(archive);
        stream.Position = 0;
        return stream;
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }

    [Fact]
    public void SameFilesDifferentInsertionOrder_ProduceSameHash()
    {
        using var streamA = BuildZip(a =>
        {
            WriteEntry(a, "b.txt", "second");
            WriteEntry(a, "a.txt", "first");
        });
        using var streamB = BuildZip(a =>
        {
            WriteEntry(a, "a.txt", "first");
            WriteEntry(a, "b.txt", "second");
        });

        using var archiveA = new ZipArchive(streamA, ZipArchiveMode.Read);
        using var archiveB = new ZipArchive(streamB, ZipArchiveMode.Read);

        Assert.Equal(CanonicalArchiveHasher.ComputeHex(archiveA), CanonicalArchiveHasher.ComputeHex(archiveB));
    }

    [Fact]
    public void SignatureEntry_IsIgnored()
    {
        using var withoutSig = BuildZip(a => WriteEntry(a, "a.txt", "content"));
        using var withSig = BuildZip(a =>
        {
            WriteEntry(a, "a.txt", "content");
            WriteEntry(a, CanonicalArchiveHasher.SignatureEntryName, "{\"anything\":true}");
        });

        using var archiveWithout = new ZipArchive(withoutSig, ZipArchiveMode.Read);
        using var archiveWith = new ZipArchive(withSig, ZipArchiveMode.Read);

        Assert.Equal(
            CanonicalArchiveHasher.ComputeHex(archiveWithout),
            CanonicalArchiveHasher.ComputeHex(archiveWith));
    }

    [Fact]
    public void OneByteDifference_ProducesDifferentHash()
    {
        using var original = BuildZip(a => WriteEntry(a, "a.txt", "content"));
        using var tampered = BuildZip(a => WriteEntry(a, "a.txt", "kontent"));

        using var archiveOriginal = new ZipArchive(original, ZipArchiveMode.Read);
        using var archiveTampered = new ZipArchive(tampered, ZipArchiveMode.Read);

        Assert.NotEqual(
            CanonicalArchiveHasher.ComputeHex(archiveOriginal),
            CanonicalArchiveHasher.ComputeHex(archiveTampered));
    }

    [Fact]
    public void DirectoryEntries_DoNotAffectHash()
    {
        using var withoutDir = BuildZip(a => WriteEntry(a, "sub/a.txt", "content"));
        using var withDir = BuildZip(a =>
        {
            a.CreateEntry("sub/"); // директория: entry.Name пусто, entry.FullName == "sub/"
            WriteEntry(a, "sub/a.txt", "content");
        });

        using var archiveWithoutDir = new ZipArchive(withoutDir, ZipArchiveMode.Read);
        using var archiveWithDir = new ZipArchive(withDir, ZipArchiveMode.Read);

        Assert.Equal(
            CanonicalArchiveHasher.ComputeHex(archiveWithoutDir),
            CanonicalArchiveHasher.ComputeHex(archiveWithDir));
    }

    [Fact]
    public void AmbiguousConcatenation_NameContentSplit_ProducesDifferentHash()
    {
        // "ab"+"" + "" (имя "ab", контент "") не должно совпасть с "a"+"b" по хешу —
        // проверяет, что имя и контент не конкатенируются без разделителя/префикса длины.
        using var variantA = BuildZip(a => WriteEntry(a, "ab", ""));
        using var variantB = BuildZip(a => WriteEntry(a, "a", "b"));

        using var archiveA = new ZipArchive(variantA, ZipArchiveMode.Read);
        using var archiveB = new ZipArchive(variantB, ZipArchiveMode.Read);

        Assert.NotEqual(
            CanonicalArchiveHasher.ComputeHex(archiveA),
            CanonicalArchiveHasher.ComputeHex(archiveB));
    }
}
```

- [ ] **Step 2: Убедиться, что тесты падают (класс ещё не существует)**

Run: `dotnet test tests/Ven4Tools.Tests --filter "FullyQualifiedName~CanonicalArchiveHasherTests"`
Expected: FAIL — `CanonicalArchiveHasher` не найден (ошибка компиляции).

- [ ] **Step 3: Реализовать `CanonicalArchiveHasher`**

```csharp
using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Ven4Tools.Launcher.Services;

/// <summary>
/// Канонический хеш содержимого zip-архива клиента, БЕЗ записи
/// _ven4tools_signature.json — детерминированная величина, над которой
/// ClientArchiveSigner ставит офлайн-подпись внутри самого архива.
/// Алгоритм ЗАФИКСИРОВАН и продублирован байт-в-байт в Tools/ClientArchiveSigner
/// (общей библиотеки между лаунчером и Tools-утилитами в проекте нет намеренно) —
/// любое изменение здесь делает несовместимыми уже подписанные архивы, менять
/// только синхронно в обоих местах.
/// </summary>
internal static class CanonicalArchiveHasher
{
    internal const string SignatureEntryName = "_ven4tools_signature.json";

    /// <summary>
    /// Порядок: записи (кроме SignatureEntryName и каталогов — пустое Name),
    /// отсортированные по FullName (Ordinal). Для каждой записи в хеш подаётся:
    /// 4-байтовая little-endian длина UTF8-имени + само имя + 8-байтовая
    /// little-endian длина содержимого + само содержимое. Длины-префиксы
    /// исключают неоднозначность конкатенации (иначе записи "ab"+"" и "a"+"b"
    /// с одинаковой суммой байт дали бы одинаковый хеш).
    /// </summary>
    public static string ComputeHex(ZipArchive archive)
    {
        var entries = archive.Entries
            .Where(e => !string.IsNullOrEmpty(e.Name))
            .Where(e => !string.Equals(e.FullName, SignatureEntryName, StringComparison.Ordinal))
            .OrderBy(e => e.FullName, StringComparer.Ordinal)
            .ToList();

        using var incremental = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> lenBuf = stackalloc byte[8];
        foreach (var entry in entries)
        {
            byte[] nameBytes = Encoding.UTF8.GetBytes(entry.FullName);
            BinaryPrimitives.WriteUInt32LittleEndian(lenBuf, (uint)nameBytes.Length);
            incremental.AppendData(lenBuf[..4]);
            incremental.AppendData(nameBytes);

            using var entryStream = entry.Open();
            using var buffered = new MemoryStream();
            entryStream.CopyTo(buffered);
            byte[] content = buffered.ToArray();
            BinaryPrimitives.WriteUInt64LittleEndian(lenBuf, (ulong)content.LongLength);
            incremental.AppendData(lenBuf);
            incremental.AppendData(content);
        }

        return Convert.ToHexString(incremental.GetHashAndReset()).ToLowerInvariant();
    }
}
```

- [ ] **Step 4: Запустить тесты, убедиться, что проходят**

Run: `dotnet test tests/Ven4Tools.Tests --filter "FullyQualifiedName~CanonicalArchiveHasherTests"`
Expected: PASS (5/5).

- [ ] **Step 5: Commit**

```bash
git add Ven4Tools.Launcher/Services/CanonicalArchiveHasher.cs tests/Ven4Tools.Tests/CanonicalArchiveHasherTests.cs
git commit -m "Лаунчер: канонический хеш содержимого архива клиента (CanonicalArchiveHasher)"
```

---

### Task 2: `version.json` — поля `revokedClientHashes` и `historicalClientArchives`

**Files:**
- Modify: `Ven4Tools.Launcher/Models/CdnVersionInfo.cs`
- ~~Modify: `version.json`, `_release/version.json`~~ (отменено — см. Step 5)
- Test: `tests/Ven4Tools.Tests/CdnVersionInfoDeserializationTests.cs`

**Interfaces:**
- Produces: `CdnVersionInfo.RevokedClientHashes : List<string>?`, `CdnVersionInfo.HistoricalClientArchives : List<HistoricalClientArchive>?`, `HistoricalClientArchive { string? Version; string? Sha256; }` — используются `LocalArchiveVerifier` (Task 5).

- [ ] **Step 1: Написать падающий тест десериализации**

```csharp
using System.Text.Json;
using Ven4Tools.Launcher.Models;

namespace Ven4Tools.Tests;

public sealed class CdnVersionInfoDeserializationTests
{
    private const string Json = """
    {
      "client": { "version": "4.4.3" },
      "launcher": { "version": "3.2.2" },
      "cdn_ip": "138.16.152.133",
      "revokedClientHashes": ["deadbeef"],
      "historicalClientArchives": [
        { "version": "4.4.2", "sha256": "ffce9133" }
      ]
    }
    """;

    [Fact]
    public void Deserialize_ReadsRevokedAndHistoricalFields()
    {
        var info = JsonSerializer.Deserialize<CdnVersionInfo>(Json);

        Assert.NotNull(info);
        Assert.Equal(["deadbeef"], info!.RevokedClientHashes);
        Assert.NotNull(info.HistoricalClientArchives);
        Assert.Single(info.HistoricalClientArchives!);
        Assert.Equal("4.4.2", info.HistoricalClientArchives![0].Version);
        Assert.Equal("ffce9133", info.HistoricalClientArchives[0].Sha256);
    }

    [Fact]
    public void Deserialize_MissingFields_YieldNull()
    {
        var info = JsonSerializer.Deserialize<CdnVersionInfo>("""{ "client": { "version": "4.4.3" } }""");

        Assert.NotNull(info);
        Assert.Null(info!.RevokedClientHashes);
        Assert.Null(info.HistoricalClientArchives);
    }
}
```

- [ ] **Step 2: Убедиться, что тесты падают**

Run: `dotnet test tests/Ven4Tools.Tests --filter "FullyQualifiedName~CdnVersionInfoDeserializationTests"`
Expected: FAIL — компиляция (свойств ещё нет).

- [ ] **Step 3: Добавить поля в модель**

В `Ven4Tools.Launcher/Models/CdnVersionInfo.cs` внутри класса `CdnVersionInfo` (после `CdnIp`):

```csharp
        [JsonPropertyName("revokedClientHashes")]
        public List<string>? RevokedClientHashes { get; set; }

        [JsonPropertyName("historicalClientArchives")]
        public List<HistoricalClientArchive>? HistoricalClientArchives { get; set; }
```

Добавить `using System.Collections.Generic;` в начало файла (если ещё не подключён), и новый класс в конец файла (тот же namespace `Ven4Tools.Launcher.Models`):

```csharp
    public class HistoricalClientArchive
    {
        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("sha256")]
        public string? Sha256 { get; set; }
    }
```

- [ ] **Step 4: Запустить тесты, убедиться, что проходят**

Run: `dotnet test tests/Ven4Tools.Tests --filter "FullyQualifiedName~CdnVersionInfoDeserializationTests"`
Expected: PASS (2/2).

- [x] **Step 5 (ОТМЕНЁН — исходное предположение плана было неверным):** ~~Обновить схему в реальных файлах `version.json`~~

При исполнении (Task 2, реализация) обнаружено: `version.json` и `_release/` — в `.gitignore` (`/version.json`, `/version.json.sig`, `_release/`, см. `.gitignore:64-65,75`) и никогда не были в git-истории (`git log --all -- version.json _release/version.json` — пусто). Это локальные, генерируемые на месте артефакты, не часть репозитория — при исследовании перед написанием плана они были прочитаны как untracked-файлы основного чекаута и ошибочно приняты за версии, живущие в репо. Редактировать и коммитить их не нужно и невозможно осмысленно (в свежем `git worktree` их вообще нет на диске — untracked-файлы между worktree не расшариваются).

Схема (`revokedClientHashes`/`historicalClientArchives`) фактически появится в реальном `version.json` естественным образом — на следующем реальном релизе, когда кто-то вручную допишет эти поля в JSON перед запуском `Tools/deploy-version-manifest.ps1` (тот подписывает и заливает файл на CDN как есть, схему не валидирует). Никакого отдельного шага/коммита в этом плане для этого не требуется — модель `CdnVersionInfo` (Step 3-4) уже готова прочитать эти поля, когда они появятся.

- [x] **Step 6: Commit**

```bash
git add Ven4Tools.Launcher/Models/CdnVersionInfo.cs tests/Ven4Tools.Tests/CdnVersionInfoDeserializationTests.cs
git commit -m "CdnVersionInfo: схема для revokedClientHashes и historicalClientArchives"
```
(Выполнено — commit `8289615`, без `version.json`/`_release/version.json`, см. правку Step 5 выше.)

---

### Task 3: `Tools/ClientArchiveSigner` — утилита подписи + реальный ключ

**Files:**
- Create: `Tools/ClientArchiveSigner/ClientArchiveSigner.csproj`
- Create: `Tools/ClientArchiveSigner/Program.cs`
- Create: `Tools/sign-client-archive.ps1`

**Interfaces:**
- Produces: CLI `ClientArchiveSigner <archive.zip> <private-key.pem> <version>` (подпись) и `ClientArchiveSigner verify <archive.zip> <public-key.pem>` (проверка) — генерирует запись `_ven4tools_signature.json` = `{"sha256_canonical","signature","version"}` внутри zip. Публичный ключ, сгенерированный здесь, встраивается в `ClientArchiveVerifier` (Task 4).

**Обоснование отклонения от исходного текста спеки:** спека описывала подписываемый payload как `DomainSeparator + H_canonical` (без версии). При таком payload поле `"version"` в `_ven4tools_signature.json` НЕ покрыто подписью — его можно отредактировать прямо в уже подписанном архиве (не трогая остальные файлы, значит не трогая `H_canonical`) без приватного ключа, и подпись всё равно останется валидной. Это не даёт подменить сами файлы клиента (за это отвечает `H_canonical`), но позволяет соврать про номер версии в логе/истории установок. Правильный payload — `DomainSeparator + version + "\n" + H_canonical`, тогда версия тоже криптографически привязана к содержимому.

- [ ] **Step 1: Сгенерировать реальный ключ ECDSA P-256 (не коммитить приватную часть)**

Run (Bash, Git Bash поставляется с openssl):
```bash
mkdir -p "$USERPROFILE/.ven4tools"
openssl ecparam -name prime256v1 -genkey -noout -out "$USERPROFILE/.ven4tools/client-archive-signing-private.pem"
openssl ec -in "$USERPROFILE/.ven4tools/client-archive-signing-private.pem" -pubout -out "$USERPROFILE/.ven4tools/client-archive-signing-public.pem"
cat "$USERPROFILE/.ven4tools/client-archive-signing-public.pem"
```
Expected: два файла созданы; вывод — PEM-блок `-----BEGIN PUBLIC KEY-----...-----END PUBLIC KEY-----`. Сохранить этот вывод — он понадобится в Task 4, Step 3 (встраивается в лаунчер как публичная константа, приватный ключ никогда не публикуется).

- [ ] **Step 2: Создать проект утилиты**

`Tools/ClientArchiveSigner/ClientArchiveSigner.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
```

Не добавлять в `Ven4Tools.sln` — как `CatalogSigner`/`UpdateManifestSigner`, standalone-проект, запускается через `dotnet run --project Tools/ClientArchiveSigner`.

- [ ] **Step 3: Написать `Program.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// Домен-сепаратор + отдельный ключ, тот же принцип, что у CatalogSigner/
// UpdateManifestSigner/NotificationsSigner. Payload включает version — иначе
// это поле в _ven4tools_signature.json можно подменить без приватного ключа,
// не трогая H_canonical (см. обоснование в плане реализации).
const string DomainSeparator = "Ven4Tools.ClientArchive.v1\n";
const string SignatureEntryName = "_ven4tools_signature.json";

if (args.Length == 3 && args[0] == "verify")
{
    string archivePathV = Path.GetFullPath(args[1]);
    string publicKeyPathV = Path.GetFullPath(args[2]);

    using var zipV = ZipFile.OpenRead(archivePathV);
    var sigEntryV = zipV.GetEntry(SignatureEntryName);
    if (sigEntryV == null)
    {
        Console.Error.WriteLine($"НЕВАЛИДНО: в архиве нет записи {SignatureEntryName}");
        return 1;
    }

    string sigJsonV;
    using (var s = sigEntryV.Open())
    using (var r = new StreamReader(s, Encoding.UTF8))
        sigJsonV = r.ReadToEnd();

    var sigDataV = JsonSerializer.Deserialize<SignatureFile>(sigJsonV);
    if (sigDataV?.sha256_canonical == null || sigDataV.signature == null || sigDataV.version == null)
    {
        Console.Error.WriteLine($"НЕВАЛИДНО: не удалось разобрать {SignatureEntryName}");
        return 1;
    }

    string computedHashV = ComputeCanonicalHashHex(zipV);
    if (!string.Equals(computedHashV, sigDataV.sha256_canonical, StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine(
            $"НЕВАЛИДНО: канонический хеш не совпал (посчитан {computedHashV}, в записи {sigDataV.sha256_canonical})");
        return 1;
    }

    using var pubKeyV = ECDsa.Create();
    pubKeyV.ImportFromPem(File.ReadAllText(publicKeyPathV));
    bool validV;
    try
    {
        validV = pubKeyV.VerifyData(
            Encoding.UTF8.GetBytes(DomainSeparator + sigDataV.version + "\n" + sigDataV.sha256_canonical),
            Convert.FromBase64String(sigDataV.signature),
            HashAlgorithmName.SHA256);
    }
    catch { validV = false; }

    if (!validV)
    {
        Console.Error.WriteLine("НЕВАЛИДНО: подпись не соответствует версии/каноническому хешу");
        return 1;
    }
    Console.WriteLine($"OK: архив версии {sigDataV.version} подписан корректно (sha256_canonical={computedHashV})");
    return 0;
}

if (args.Length != 3)
{
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  ClientArchiveSigner <archive.zip> <private-key.pem> <version>");
    Console.Error.WriteLine("  ClientArchiveSigner verify <archive.zip> <public-key.pem>");
    return 2;
}

string archivePath = Path.GetFullPath(args[0]);
string privateKeyPath = Path.GetFullPath(args[1]);
string version = args[2];

string canonicalHashHex;
using (var zipRead = ZipFile.OpenRead(archivePath))
    canonicalHashHex = ComputeCanonicalHashHex(zipRead);

using var key = ECDsa.Create();
key.ImportFromPem(File.ReadAllText(privateKeyPath));
byte[] signature = key.SignData(
    Encoding.UTF8.GetBytes(DomainSeparator + version + "\n" + canonicalHashHex),
    HashAlgorithmName.SHA256);

string signatureJson = JsonSerializer.Serialize(new SignatureFile
{
    sha256_canonical = canonicalHashHex,
    signature = Convert.ToBase64String(signature),
    version = version
});

using (var zipWrite = ZipFile.Open(archivePath, ZipArchiveMode.Update))
{
    zipWrite.GetEntry(SignatureEntryName)?.Delete();
    var newEntry = zipWrite.CreateEntry(SignatureEntryName, CompressionLevel.Optimal);
    using var w = new StreamWriter(newEntry.Open(), Encoding.UTF8);
    w.Write(signatureJson);
}

Console.WriteLine($"Подписано: {archivePath} (версия {version}, sha256_canonical={canonicalHashHex})");
return 0;

// ── канонический хеш — ТА ЖЕ логика, что в Ven4Tools.Launcher/Services/
// CanonicalArchiveHasher.cs. Общей библиотеки между Tools/* и лаунчером нет —
// при изменении менять синхронно в обоих местах, иначе уже подписанные
// архивы перестанут проходить проверку в LocalArchiveVerifier.
static string ComputeCanonicalHashHex(ZipArchive archive)
{
    var entries = new List<ZipArchiveEntry>();
    foreach (var e in archive.Entries)
    {
        if (string.IsNullOrEmpty(e.Name)) continue;
        if (string.Equals(e.FullName, SignatureEntryName, StringComparison.Ordinal)) continue;
        entries.Add(e);
    }
    entries.Sort((a, b) => string.CompareOrdinal(a.FullName, b.FullName));

    using var incremental = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    Span<byte> lenBuf = stackalloc byte[8];
    foreach (var entry in entries)
    {
        byte[] nameBytes = Encoding.UTF8.GetBytes(entry.FullName);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(lenBuf, (uint)nameBytes.Length);
        incremental.AppendData(lenBuf[..4]);
        incremental.AppendData(nameBytes);

        using var entryStream = entry.Open();
        using var buffered = new MemoryStream();
        entryStream.CopyTo(buffered);
        byte[] content = buffered.ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(lenBuf, (ulong)content.LongLength);
        incremental.AppendData(lenBuf);
        incremental.AppendData(content);
    }
    return Convert.ToHexString(incremental.GetHashAndReset()).ToLowerInvariant();
}

class SignatureFile
{
    public string? sha256_canonical { get; set; }
    public string? signature { get; set; }
    public string? version { get; set; }
}
```

- [ ] **Step 4: Смоук-тест утилиты вручную (не автотест — тот же паттерн, что у `CatalogSigner`/`UpdateManifestSigner`, которые тоже не покрыты прямыми юнит-тестами Program.cs)**

Run:
```bash
cd "%USERPROFILE%\Documents\GitHub\Ven4Tools"
mkdir -p /tmp/casigner-smoke && cd /tmp/casigner-smoke
echo "dummy" > file.txt
7z a -tzip smoke.zip file.txt || powershell -Command "Compress-Archive -Path file.txt -DestinationPath smoke.zip"
dotnet run --project "%USERPROFILE%\Documents\GitHub\Ven4Tools\Tools\ClientArchiveSigner" -- smoke.zip "$USERPROFILE/.ven4tools/client-archive-signing-private.pem" 9.9.9-smoke
dotnet run --project "%USERPROFILE%\Documents\GitHub\Ven4Tools\Tools\ClientArchiveSigner" -- verify smoke.zip "$USERPROFILE/.ven4tools/client-archive-signing-public.pem"
```
Expected: первая команда печатает `Подписано: .../smoke.zip (версия 9.9.9-smoke, sha256_canonical=...)`; вторая печатает `OK: архив версии 9.9.9-smoke подписан корректно (...)`, код возврата 0.

- [ ] **Step 5: Написать `Tools/sign-client-archive.ps1`**

```powershell
<#
.SYNOPSIS
Подписывает уже собранный zip-архив клиента ECDSA-ключом (Ven4Tools.ClientArchive.v1)
для последующей офлайн-установки из локального файла в лаунчере.

.DESCRIPTION
Запускать ПОСЛЕ обычной сборки zip (dotnet publish + Compress-Archive), ДО подсчёта
whole-file SHA256 для version.json (см. deploy-version-manifest.ps1) — подпись
дописывается внутрь архива как новая запись, поэтому исходный файл после этого шага
на одну запись длиннее, и whole-file SHA256 нужно считать уже над этим, финальным
файлом.

.EXAMPLE
.\Tools\sign-client-archive.ps1 -ArchivePath .\_release\Ven4Tools-Client-4.4.3.zip -Version 4.4.3
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$ArchivePath,

    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$PrivateKeyPath = "$env:USERPROFILE\.ven4tools\client-archive-signing-private.pem",
    [string]$PublicKeyPath = "$env:USERPROFILE\.ven4tools\client-archive-signing-public.pem",
    [string]$SignerDll = "$PSScriptRoot\ClientArchiveSigner\bin\Release\net8.0\ClientArchiveSigner.dll"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $ArchivePath)) { throw "Не найден $ArchivePath" }
if (-not (Test-Path $PrivateKeyPath)) {
    throw "Не найден приватный ключ подписи архива клиента: $PrivateKeyPath. " +
          "Ключ не хранится в репозитории — он должен быть на этой машине отдельно."
}
if (-not (Test-Path $SignerDll)) {
    Write-Host "ClientArchiveSigner не собран — собираю..."
    dotnet build "$PSScriptRoot\ClientArchiveSigner\ClientArchiveSigner.csproj" -c Release --nologo | Out-Null
}

Write-Host "Подписываю $ArchivePath (версия $Version)..."
dotnet $SignerDll $ArchivePath $PrivateKeyPath $Version
if ($LASTEXITCODE -ne 0) { throw "Подпись не создана — проверь вывод ClientArchiveSigner выше." }

Write-Host "Проверяю подпись локально..."
dotnet $SignerDll verify $ArchivePath $PublicKeyPath
if ($LASTEXITCODE -ne 0) { throw "Локальная подпись не прошла проверку." }

Write-Host "Готово. Архив подписан — теперь считайте whole-file SHA256 для version.json над ЭТИМ файлом."
```

- [ ] **Step 6: Commit**

```bash
git add Tools/ClientArchiveSigner Tools/sign-client-archive.ps1
git commit -m "Tools: утилита подписи архива клиента (ClientArchiveSigner) для офлайн-установки"
```

Приватный ключ (`$USERPROFILE\.ven4tools\client-archive-signing-private.pem`) НЕ добавлять в git — путь вне репозитория, `git add` его физически не затронет, но проверить `git status` перед коммитом на всякий случай.

---

### Task 4: `ClientArchiveVerifier` — встроенный публичный ключ + фикстуры

**Files:**
- Create: `Ven4Tools.Launcher/Models/ClientArchiveSignatureFile.cs`
- Create: `Ven4Tools.Launcher/Services/ClientArchiveVerifier.cs`
- Create: `tests/Ven4Tools.Tests/Fixtures/client-archive-signed-sample.zip`
- Create: `tests/Ven4Tools.Tests/Fixtures/client-archive-unsigned-sample.zip`
- Test: `tests/Ven4Tools.Tests/ClientArchiveVerifierTests.cs`
- Modify: `tests/Ven4Tools.Tests/Ven4Tools.Tests.csproj` (добавить `<None Include>` для новых фикстур, по образцу существующих записей для `Fixtures/version-manifest-sample.json`)

**Interfaces:**
- Consumes: `CanonicalArchiveHasher.ComputeHex` (Task 1), `EcdsaManifestVerifier.Verify(publicKeyPem, domainSeparator, payload, signature)` (существующий, `Ven4Tools.Launcher/Services/EcdsaManifestVerifier.cs`).
- Produces: `internal static class ClientArchiveVerifier { public static bool Verify(string version, string canonicalHashHex, string? signature); }`, `internal sealed class ClientArchiveSignatureFile { string? Sha256Canonical; string? Signature; string? Version; }` — используются `LocalArchiveVerifier` (Task 5).

- [ ] **Step 1: Создать фикстуры (реальным продакшн-ключом из Task 3)**

Run:
```bash
cd "%USERPROFILE%\Documents\GitHub\Ven4Tools"
mkdir -p /tmp/casigner-fixture && cd /tmp/casigner-fixture
mkdir payload && echo "test client payload" > payload/dummy.txt
powershell -Command "Compress-Archive -Path payload\* -DestinationPath signed.zip -Force"
powershell -Command "Compress-Archive -Path payload\* -DestinationPath unsigned.zip -Force"
dotnet run --project "%USERPROFILE%\Documents\GitHub\Ven4Tools\Tools\ClientArchiveSigner" -- signed.zip "$USERPROFILE/.ven4tools/client-archive-signing-private.pem" 9.9.9-fixture
cp signed.zip "%USERPROFILE%\Documents\GitHub\Ven4Tools\tests\Ven4Tools.Tests\Fixtures\client-archive-signed-sample.zip"
cp unsigned.zip "%USERPROFILE%\Documents\GitHub\Ven4Tools\tests\Ven4Tools.Tests\Fixtures\client-archive-unsigned-sample.zip"
```
Expected: два `.zip`-файла скопированы в `Fixtures/`. `unsigned.zip` — байт-в-байт та же полезная нагрузка, но без записи `_ven4tools_signature.json`. Подписание тестовой фикстуры реальным продакшн-приватным ключом безопасно — ECDSA-подпись не раскрывает сам ключ.

- [ ] **Step 2: Добавить фикстуры в `.csproj` теста**

В `tests/Ven4Tools.Tests/Ven4Tools.Tests.csproj`, рядом с существующими `<None Include>` для `Fixtures/version-manifest-sample.json`, добавить аналогичные записи для двух новых файлов (копирование в output directory при тесте — `CopyToOutputDirectory` тем же способом, что у существующих фикстур).

- [ ] **Step 3: Модель `_ven4tools_signature.json`**

`Ven4Tools.Launcher/Models/ClientArchiveSignatureFile.cs`:
```csharp
using System.Text.Json.Serialization;

namespace Ven4Tools.Launcher.Models;

internal sealed class ClientArchiveSignatureFile
{
    [JsonPropertyName("sha256_canonical")]
    public string? Sha256Canonical { get; set; }

    [JsonPropertyName("signature")]
    public string? Signature { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }
}
```

- [ ] **Step 4: Написать падающий тест `ClientArchiveVerifierTests`**

```csharp
using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Tests;

public sealed class ClientArchiveVerifierTests
{
    [Fact]
    public void FixtureSignature_IsValid()
    {
        // Значения соответствуют tests/Ven4Tools.Tests/Fixtures/client-archive-signed-sample.zip,
        // сгенерированному в Task 4, Step 1 (версия "9.9.9-fixture").
        // canonicalHashHex и signature читаются тестом ClientArchiveHasherReadsFixture ниже —
        // тут проверяем саму функцию Verify на заведомо валидной паре.
        Assert.True(true); // placeholder заменяется в Step 6 реальными значениями из фикстуры
    }
}
```

(Реальное содержимое теста уточняется в Step 6 — оно зависит от фактических байт фикстуры, созданной в Step 1; см. там.)

- [ ] **Step 5: Реализовать `ClientArchiveVerifier`**

```csharp
namespace Ven4Tools.Launcher.Services;

/// <summary>
/// Проверка офлайн-подписи архива клиента (Ven4Tools.ClientArchive.v1).
/// Публичный ключ ниже сгенерирован при внедрении фичи локальной установки —
/// см. docs/superpowers/plans/2026-08-02-local-client-archive-install.md, Task 3.
/// Приватный ключ никогда не публикуется, хранится вне репозитория на машине
/// разработчика (Tools/sign-client-archive.ps1).
/// </summary>
internal static class ClientArchiveVerifier
{
    private const string DomainSeparator = "Ven4Tools.ClientArchive.v1\n";

    // ЗАМЕНИТЬ на реальный вывод Task 3, Step 1 (openssl ec ... -pubout) —
    // PEM-блок публичного ключа, сгенерированного для этой фичи.
    private const string PublicKey = """
        -----BEGIN PUBLIC KEY-----
        ЗАМЕНИТЬ_НА_РЕАЛЬНЫЙ_КЛЮЧ_ИЗ_TASK_3
        -----END PUBLIC KEY-----
        """;

    public static bool Verify(string version, string canonicalHashHex, string? signature) =>
        EcdsaManifestVerifier.Verify(
            PublicKey, DomainSeparator, version + "\n" + canonicalHashHex, signature);
}
```

**Важно:** константа `PublicKey` заполняется РЕАЛЬНЫМ значением из Task 3, Step 1 — это единственное место в плане, где значение подставляется по факту выполнения предыдущего шага (значение существует только после генерации ключа, не может быть известно на момент написания плана). Скопировать ровно то, что вывела команда `cat .../client-archive-signing-public.pem`.

- [ ] **Step 6: Дописать реальный тест по фикстуре, запустить, проверить прохождение**

Заменить содержимое `ClientArchiveVerifierTests.cs` из Step 4 на:
```csharp
using System.IO.Compression;
using System.Text.Json;
using Ven4Tools.Launcher.Models;
using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Tests;

public sealed class ClientArchiveVerifierTests
{
    private static (string CanonicalHash, ClientArchiveSignatureFile Signature) ReadFixture(string fileName)
    {
        using var archive = ZipFile.OpenRead(FixturePath(fileName));
        var entry = archive.GetEntry(CanonicalArchiveHasher.SignatureEntryName)
            ?? throw new InvalidOperationException("Фикстура без подписи — используйте signed-фикстуру.");
        using var reader = new StreamReader(entry.Open());
        var signature = JsonSerializer.Deserialize<ClientArchiveSignatureFile>(reader.ReadToEnd())!;
        string canonicalHash = CanonicalArchiveHasher.ComputeHex(archive);
        return (canonicalHash, signature);
    }

    private static string FixturePath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);

    [Fact]
    public void FixtureSignature_IsValid()
    {
        var (hash, sig) = ReadFixture("client-archive-signed-sample.zip");
        Assert.True(ClientArchiveVerifier.Verify(sig.Version!, hash, sig.Signature));
    }

    [Fact]
    public void WrongVersion_IsRejected()
    {
        var (hash, sig) = ReadFixture("client-archive-signed-sample.zip");
        Assert.False(ClientArchiveVerifier.Verify("9.9.9-different", hash, sig.Signature));
    }

    [Fact]
    public void TamperedHash_IsRejected()
    {
        var (hash, sig) = ReadFixture("client-archive-signed-sample.zip");
        Assert.False(ClientArchiveVerifier.Verify(sig.Version!, hash + "00", sig.Signature));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-base64")]
    public void MalformedSignature_IsRejected(string? signature)
    {
        var (hash, sig) = ReadFixture("client-archive-signed-sample.zip");
        Assert.False(ClientArchiveVerifier.Verify(sig.Version!, hash, signature));
    }
}
```

Run: `dotnet test tests/Ven4Tools.Tests --filter "FullyQualifiedName~ClientArchiveVerifierTests"`
Expected: PASS (4/4). Если `FixtureSignature_IsValid` падает — константа `PublicKey` в Step 5 не соответствует реально использованному в Step 1 приватному ключу, перепроверить.

- [ ] **Step 7: Commit**

```bash
git add Ven4Tools.Launcher/Models/ClientArchiveSignatureFile.cs Ven4Tools.Launcher/Services/ClientArchiveVerifier.cs tests/Ven4Tools.Tests/ClientArchiveVerifierTests.cs tests/Ven4Tools.Tests/Fixtures/client-archive-signed-sample.zip tests/Ven4Tools.Tests/Fixtures/client-archive-unsigned-sample.zip tests/Ven4Tools.Tests/Ven4Tools.Tests.csproj
git commit -m "Лаунчер: ClientArchiveVerifier с встроенным публичным ключом + тестовые фикстуры"
```

---

### Task 5: `LocalArchiveVerifier` — оркестрация проверки

**Files:**
- Create: `Ven4Tools.Launcher/Services/LocalArchiveVerifier.cs`
- Test: `tests/Ven4Tools.Tests/LocalArchiveVerifierTests.cs`

**Interfaces:**
- Consumes: `CanonicalArchiveHasher.ComputeHex` (Task 1), `ClientArchiveVerifier.Verify` (Task 4), `CdnService.GetVersionInfoAsync(CancellationToken)` (существующий, возвращает `CdnVersionInfo?`), `CdnVersionInfo.RevokedClientHashes`/`HistoricalClientArchives` (Task 2).
- Produces:
```csharp
internal enum LocalArchiveOutcome { Rejected, Offline, Historical }

internal readonly struct LocalArchiveVerificationResult
{
    public LocalArchiveOutcome Outcome { get; init; }
    public string? Version { get; init; }
    public string? RejectionReason { get; init; }
}

internal static class LocalArchiveVerifier
{
    public static Task<LocalArchiveVerificationResult> VerifyAsync(
        string archivePath, CdnService cdnService, CancellationToken token);
}
```
Используется `MainWindow.Download.LocalArchive.cs` (Task 6) и `CliInstallRunner` (Task 7).

- [ ] **Step 1: Написать падающие тесты**

```csharp
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Tests;

public sealed class LocalArchiveVerifierTests
{
    private sealed class DelegateHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _response;
        public DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> response) => _response = response;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(_response(request));
    }

    private static string CopyFixture(string fileName)
    {
        string source = Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
        string dest = Path.Combine(Path.GetTempPath(), $"lavtest_{Guid.NewGuid():N}.zip");
        File.Copy(source, dest);
        return dest;
    }

    // ИСПРАВЛЕНО при исполнении (найдено имплементором Task 5): порча поля
    // "sha256_canonical" не ломает тест — LocalArchiveVerifier проверяет подпись
    // против ЖИВОГО пересчитанного canonicalHashHex, а не против значения из
    // самого файла (осознанное решение — иначе подмена этой декоративной метки
    // не требовала бы приватного ключа). Значит порча именно этого поля не меняет
    // исход: подпись всё равно проходит по Version+Signature+пересчитанному хешу,
    // и тест с оригинальной подменой поля молча проверял бы не то (Offline вместо
    // Rejected). Портить нужно "signature" — единственное поле, реально участвующее
    // в ECDSA-проверке.
    private static void FlipByteInSignatureEntry(string zipPath)
    {
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Update);
        var entry = archive.GetEntry(CanonicalArchiveHasher.SignatureEntryName)!;
        string json;
        using (var reader = new StreamReader(entry.Open())) json = reader.ReadToEnd();
        entry.Delete();
        var newEntry = archive.CreateEntry(CanonicalArchiveHasher.SignatureEntryName);
        using var writer = new StreamWriter(newEntry.Open());
        writer.Write(json.Replace("\"signature\"", "\"signature_TAMPERED\"")); // ломает поле, реально проверяемое ECDSA
    }

    // CdnService не абстрагирован интерфейсом — тесты, которым сеть не нужна
    // (валидная офлайн-подпись без проверки отзыва), передают null и полагаются
    // на то, что LocalArchiveVerifier не обращается к cdnService на этой ветке.
    [Fact]
    public async Task ValidOfflineSignature_WithoutNetwork_Succeeds()
    {
        string archive = CopyFixture("client-archive-signed-sample.zip");
        try
        {
            var result = await LocalArchiveVerifier.VerifyAsync(archive, cdnService: null!, CancellationToken.None);
            Assert.Equal(LocalArchiveOutcome.Offline, result.Outcome);
            Assert.Equal("9.9.9-fixture", result.Version);
        }
        finally { File.Delete(archive); }
    }

    [Fact]
    public async Task TamperedSignatureEntry_IsRejected()
    {
        string archive = CopyFixture("client-archive-signed-sample.zip");
        try
        {
            FlipByteInSignatureEntry(archive);
            var result = await LocalArchiveVerifier.VerifyAsync(archive, cdnService: null!, CancellationToken.None);
            Assert.Equal(LocalArchiveOutcome.Rejected, result.Outcome);
        }
        finally { File.Delete(archive); }
    }

    [Fact]
    public async Task MissingSignature_NoNetwork_IsRejected()
    {
        string archive = CopyFixture("client-archive-unsigned-sample.zip");
        try
        {
            // cdnService создан с транспортом, который всегда падает — имитация "сети нет".
            using var http = new HttpClient(new DelegateHandler(
                _ => throw new HttpRequestException("нет сети")));
            var cdn = new CdnService(); // публичный конструктор без параметров транспорта —
                                          // см. примечание в Step 3 про необходимость DI-параметра.
            var result = await LocalArchiveVerifier.VerifyAsync(archive, cdn, CancellationToken.None);
            Assert.Equal(LocalArchiveOutcome.Rejected, result.Outcome);
            Assert.Contains("сеть", result.RejectionReason, StringComparison.OrdinalIgnoreCase);
        }
        finally { File.Delete(archive); }
    }
}
```

- [ ] **Step 2: Убедиться, что тесты падают**

Run: `dotnet test tests/Ven4Tools.Tests --filter "FullyQualifiedName~LocalArchiveVerifierTests"`
Expected: FAIL — `LocalArchiveVerifier` не существует.

- [ ] **Step 3: Реализовать `LocalArchiveVerifier`**

```csharp
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Ven4Tools.Launcher.Models;

namespace Ven4Tools.Launcher.Services;

internal enum LocalArchiveOutcome { Rejected, Offline, Historical }

internal readonly struct LocalArchiveVerificationResult
{
    public LocalArchiveOutcome Outcome { get; init; }
    public string? Version { get; init; }
    public string? RejectionReason { get; init; }

    public static LocalArchiveVerificationResult Reject(string reason) =>
        new() { Outcome = LocalArchiveOutcome.Rejected, RejectionReason = reason };
}

/// <summary>
/// Проверка локального архива клиента перед установкой: сначала встроенная
/// офлайн-подпись (CanonicalArchiveHasher + ClientArchiveVerifier), при её
/// отсутствии — обязательная сетевая сверка с historicalClientArchives
/// (архивы, выпущенные до появления офлайн-подписи). В обоих случаях —
/// best-effort сверка с revokedClientHashes. См.
/// docs/superpowers/specs/2026-08-02-local-client-archive-install-design.md.
/// </summary>
internal static class LocalArchiveVerifier
{
    public static async Task<LocalArchiveVerificationResult> VerifyAsync(
        string archivePath, CdnService cdnService, CancellationToken token)
    {
        string wholeFileSha256 = await ComputeWholeFileSha256Async(archivePath, token);

        ClientArchiveSignatureFile? signatureFile;
        string canonicalHashHex;
        using (var archive = ZipFile.OpenRead(archivePath))
        {
            var entry = archive.GetEntry(CanonicalArchiveHasher.SignatureEntryName);
            signatureFile = entry == null ? null : TryReadSignatureFile(entry);
            canonicalHashHex = CanonicalArchiveHasher.ComputeHex(archive);
        }

        bool offlineValid = signatureFile?.Version != null &&
            ClientArchiveVerifier.Verify(signatureFile.Version, canonicalHashHex, signatureFile.Signature);

        if (offlineValid)
        {
            var info = await TryGetVersionInfoAsync(cdnService, token);
            if (IsRevoked(info, wholeFileSha256))
                return LocalArchiveVerificationResult.Reject(
                    "Эта версия отозвана — скачайте актуальную через обычную загрузку.");

            return new LocalArchiveVerificationResult
            {
                Outcome = LocalArchiveOutcome.Offline,
                Version = signatureFile!.Version
            };
        }

        // Нет валидной офлайн-подписи — сеть здесь ОБЯЗАТЕЛЬНА (единственный
        // источник доверия для архивов без встроенной подписи).
        var networkInfo = await TryGetVersionInfoAsync(cdnService, token);
        if (networkInfo == null)
            return LocalArchiveVerificationResult.Reject(
                "Архив без офлайн-подписи, а сеть для проверки исторического списка версий недоступна — установка отменена.");

        if (IsRevoked(networkInfo, wholeFileSha256))
            return LocalArchiveVerificationResult.Reject(
                "Эта версия отозвана — скачайте актуальную через обычную загрузку.");

        var historicalMatch = networkInfo.HistoricalClientArchives?.FirstOrDefault(h =>
            string.Equals(h.Sha256, wholeFileSha256, StringComparison.OrdinalIgnoreCase));
        if (historicalMatch == null)
            return LocalArchiveVerificationResult.Reject(
                "Архив без офлайн-подписи и не значится в списке ранее опубликованных версий — установка отменена.");

        return new LocalArchiveVerificationResult
        {
            Outcome = LocalArchiveOutcome.Historical,
            Version = historicalMatch.Version
        };
    }

    private static bool IsRevoked(CdnVersionInfo? info, string wholeFileSha256) =>
        info?.RevokedClientHashes?.Contains(wholeFileSha256, StringComparer.OrdinalIgnoreCase) == true;

    private static async Task<CdnVersionInfo?> TryGetVersionInfoAsync(CdnService? cdnService, CancellationToken token)
    {
        if (cdnService == null) return null;
        try { return await cdnService.GetVersionInfoAsync(token); }
        catch { return null; }
    }

    private static ClientArchiveSignatureFile? TryReadSignatureFile(ZipArchiveEntry entry)
    {
        try
        {
            using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return JsonSerializer.Deserialize<ClientArchiveSignatureFile>(reader.ReadToEnd());
        }
        catch { return null; }
    }

    private static async Task<string> ComputeWholeFileSha256Async(string path, CancellationToken token)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true);
        byte[] hash = await SHA256.HashDataAsync(stream, token);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
```

**Примечание по тестируемости (важно для Step 1/4):** `CdnService` в текущем виде — конкретный класс без интерфейса и без возможности подставить фейковый `HttpClient` в конструктор (см. `Ven4Tools.Launcher/Services/CdnService.cs` — `_httpClient` приватный статический). Тест `MissingSignature_NoNetwork_IsRejected` из Step 1, как написан, реально обратится к настоящему `cdn.ven4tools.ru` (или упадёт по таймауту в CI-песочнице без сети — тоже даёт `Rejected`, но не по причине, которую тест закладывал). Это ограничение существующего кода, а не новой фичи — почини́ть его здесь, не расширяя scope: передавать `null` вместо `CdnService` и трактовать `null` как «сети нет» — `TryGetVersionInfoAsync` уже это делает (Step 3 выше, `if (cdnService == null) return null;`). Обновить Step 1 теста `MissingSignature_NoNetwork_IsRejected`, чтобы он передавал `cdnService: null!`, а не реальный экземпляр:

```csharp
    [Fact]
    public async Task MissingSignature_NoNetwork_IsRejected()
    {
        string archive = CopyFixture("client-archive-unsigned-sample.zip");
        try
        {
            var result = await LocalArchiveVerifier.VerifyAsync(archive, cdnService: null!, CancellationToken.None);
            Assert.Equal(LocalArchiveOutcome.Rejected, result.Outcome);
            Assert.Contains("сеть", result.RejectionReason, StringComparison.OrdinalIgnoreCase);
        }
        finally { File.Delete(archive); }
    }
```
Удалить из этого теста неиспользуемый `DelegateHandler`/`HttpClient` (были нужны только для несостоявшегося DI-подхода) — итоговый файл теста не содержит `DelegateHandler` вовсе.

- [ ] **Step 4: Запустить тесты, убедиться, что проходят**

Run: `dotnet test tests/Ven4Tools.Tests --filter "FullyQualifiedName~LocalArchiveVerifierTests"`
Expected: PASS (3/3).

- [ ] **Step 5: Commit**

```bash
git add Ven4Tools.Launcher/Services/LocalArchiveVerifier.cs tests/Ven4Tools.Tests/LocalArchiveVerifierTests.cs
git commit -m "Лаунчер: LocalArchiveVerifier — офлайн-подпись + сетевой allow-list архивных версий"
```

---

### Task 6: Общий путь распаковки/установки + UI-кнопка «Установить из файла»

**Files:**
- Modify: `Ven4Tools.Launcher/MainWindow.Download.cs`
- Create: `Ven4Tools.Launcher/MainWindow.Download.LocalArchive.cs`
- Modify: `Ven4Tools.Launcher/MainWindow.xaml`

**Interfaces:**
- Consumes: `LocalArchiveVerifier.VerifyAsync` (Task 5), существующие `SafeZipExtractor.ExtractAsync`, `TransactionalDirectoryInstaller.Install`, `InstallPathGuard.IsClientPathSafe`, `IsClientRunning()`, `TryCloseRunningClientAsync()`, `SetOperationStage(int)`, `SetLaunchButtonState(...)` — всё уже существует в `MainWindow`.
- Produces: `private async Task<bool> ExtractAndInstallClientAsync(string sourceArchivePath, string versionLabel, CancellationToken token, bool silent)` (новый общий метод в `MainWindow.Download.cs`) — используется и `DownloadVersionAsync` (рефакторится на его вызов), и новым `InstallFromLocalArchiveAsync` (Task 6), и `CliInstallRunner` (Task 7, через `MainWindow`-независимый headless-путь — см. примечание в Task 7).

- [ ] **Step 1: Вынести общий хвост из `DownloadVersionAsync` в `ExtractAndInstallClientAsync`**

В `Ven4Tools.Launcher/MainWindow.Download.cs` заменить блок от `SetOperationStage(3); // Распаковка` (строка ~199) до конца `try` перед `SetLaunchButtonState`/финальным `MessageBox.Show` (строки ~199–278 в текущем файле) на вызов нового метода. Новый метод добавляется в конец класса (после `DownloadVersionAsync`, перед закрывающей `}` класса):

```csharp
        // Общий хвост «распаковка → закрыть запущенный клиент → проверка пути →
        // атомарная установка», используемый и сетевой загрузкой (DownloadVersionAsync),
        // и локальной установкой из файла (InstallFromLocalArchiveAsync), и CLI
        // --install-from — единый путь, чтобы не плодить два параллельных места
        // с риском разойтись друг с другом (тот же класс проблемы, что был найден
        // и исправлен в InstallationService.Choco.cs/.Winget.cs в раунде аудита
        // 2026-08-02, см. docs/superpowers/specs/2026-08-02-local-client-archive-install-design.md).
        // sourceArchivePath — уже проверенный (SHA256 для сетевого пути, LocalArchiveVerifier
        // для локального) архив на диске, готовый к распаковке без дальнейших проверок.
        private async Task<bool> ExtractAndInstallClientAsync(
            string sourceArchivePath, string versionLabel, CancellationToken token, bool silent)
        {
            string clientParent = Path.GetDirectoryName(Path.GetFullPath(_clientPath))
                ?? throw new InvalidOperationException("Не удалось определить каталог установки.");
            string extractPath = Path.Combine(
                clientParent, $".Ven4Tools_Client.staging-{Guid.NewGuid():N}");

            try
            {
                SetOperationStage(3); // Распаковка
                txtDownloadStatus.Text = "Распаковка...";
                await SafeZipExtractor.ExtractAsync(sourceArchivePath, extractPath, token);
                AddLog("✅ Архив безопасно распакован");

                token.ThrowIfCancellationRequested();

                if (IsClientRunning())
                {
                    txtDownloadStatus.Text = "Клиент запущен";

                    if (silent)
                    {
                        SetOperationStage(0);
                        AddLog("⏸ Установка отложена: клиент запущен");
                        return false;
                    }

                    var answer = System.Windows.MessageBox.Show(
                        "Ven4Tools сейчас запущен.\n\nЗакрыть клиент сейчас, чтобы установить эту версию?",
                        "Клиент запущен", MessageBoxButton.YesNo, MessageBoxImage.Question);

                    if (answer != MessageBoxResult.Yes)
                    {
                        SetOperationStage(0);
                        AddLog("⏹ Установка отменена — клиент не закрыт");
                        return false;
                    }

                    AddLog("🔒 Закрываю клиент перед установкой...");
                    if (!await TryCloseRunningClientAsync())
                    {
                        txtDownloadStatus.Text = "Клиент запущен";
                        SetOperationStage(0);
                        AddLog("⚠️ Клиент не закрылся за отведённое время — установка отменена");
                        if (!silent)
                            System.Windows.MessageBox.Show(
                                "Не удалось закрыть клиент автоматически (возможно, он свёрнут в трей).\n\n" +
                                "Закройте его вручную и повторите установку.",
                                "Клиент не закрылся", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return false;
                    }
                    AddLog("✅ Клиент закрыт, продолжаю установку");
                }

                if (!InstallPathGuard.IsClientPathSafe(_clientPath, _dataFolderPath))
                {
                    txtDownloadStatus.Text = "Ошибка пути";
                    SetOperationStage(0);
                    AddLog($"⛔ Папка установки клиента пересекается с папкой данных — установка отменена: {_clientPath}");
                    if (!silent)
                        System.Windows.MessageBox.Show(
                            $"Папка установки клиента:\n{_clientPath}\n\nсовпадает или вложена в папку данных Ven4Tools. " +
                            "Установка отменена во избежание потери настроек.\n\nВыберите другую папку установки.",
                            "Небезопасный путь установки", MessageBoxButton.OK, MessageBoxImage.Error);
                    return false;
                }

                SetOperationStage(4); // Установка файлов
                txtDownloadStatus.Text = "Установка файлов...";
                var installer = new TransactionalDirectoryInstaller();
                installer.Install(extractPath, _clientPath, token);

                SetOperationStage(5); // Готово
                txtDownloadStatus.Text = "Готово";
                progressDownload.Value = 100;
                AddLog($"✅ Клиент {versionLabel} установлен");

                SetLaunchButtonState(LaunchButtonState.Launch);
                _clientUpdateAvailable = false;
                return true;
            }
            finally
            {
                for (int attempt = 1; attempt <= 5; attempt++)
                {
                    try
                    {
                        if (Directory.Exists(extractPath)) Directory.Delete(extractPath, true);
                        break;
                    }
                    catch (IOException) when (attempt < 5)
                    {
                        try { await Task.Delay(1000); } catch { }
                    }
                    catch { break; }
                }
            }
        }
```

`DownloadVersionAsync` после рефакторинга (заменяет вырезанный блок):
```csharp
                bool installed = await ExtractAndInstallClientAsync(tempZip, version.Version, token, silent);
                if (!installed) return;

                if (!silent)
                    System.Windows.MessageBox.Show(
                        $"Клиент {version.Version} успешно установлен в:\n{_clientPath}",
                        "Установка завершена", MessageBoxButton.OK, MessageBoxImage.Information);
```
(остаётся `catch`/`finally` блоки `DownloadVersionAsync` как были — они отвечают за скачивание/SHA256-этап, не за распаковку).

- [ ] **Step 2: Собрать и прогнать существующие тесты — регрессии быть не должно**

Run: `dotnet build Ven4Tools.sln -c Release` — 0 ошибок, 0 предупреждений.
Run: `dotnet test tests/Ven4Tools.Tests --filter "FullyQualifiedName~FallbackDownloader|FullyQualifiedName~TransactionalDirectoryInstaller"`
Expected: PASS, без изменений в количестве тестов относительно состояния до рефакторинга.

- [ ] **Step 3: Добавить `ResolveClientPath()` в `LauncherPaths` (переиспользуется в Task 7)**

В `Ven4Tools.Launcher/Services/LauncherPaths.cs` добавить (та же логика, что уже инлайн в `MainWindow.xaml.cs` при инициализации `_installPath`/`_clientPath`):
```csharp
        /// <summary>
        /// Путь к папке клиента: из настроек (launcher_settings.json → InstallPath),
        /// либо, если не задан, рядом с исполняемым файлом лаунчера — та же логика,
        /// что использует MainWindow при старте. Общий метод, чтобы CliInstallRunner
        /// не дублировал резолвинг мимо MainWindow и не расходился с ним.
        /// </summary>
        public static string ResolveClientPath()
        {
            string appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ven4Tools");
            string settingsPath = Path.Combine(appData, "launcher_settings.json");
            string installPath = AppDomain.CurrentDomain.BaseDirectory;
            try
            {
                if (File.Exists(settingsPath))
                {
                    var json = File.ReadAllText(settingsPath);
                    var settings = Newtonsoft.Json.Linq.JObject.Parse(json);
                    string? fromSettings = settings["InstallPath"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(fromSettings)) installPath = fromSettings;
                }
            }
            catch { }
            return Path.Combine(installPath, "Ven4Tools_Client");
        }
```

- [ ] **Step 4: Добавить кнопку в XAML**

В `Ven4Tools.Launcher/MainWindow.xaml`, сразу после `btnLaunchApp` (строка ~59, тот же `StackPanel Grid.Row="1"`):
```xml
                    <Button x:Name="btnInstallFromFile" Content="Установить из файла..." Height="32" Margin="0,0,0,8"
                            ToolTip="Установить клиента из уже скачанного вами архива (.zip) — на случай, если обычная загрузка недоступна лаунчеру, но архив у вас уже есть другим путём."
                            Click="BtnInstallFromFile_Click"/>
```

- [ ] **Step 5: Создать `MainWindow.Download.LocalArchive.cs`**

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Launcher
{
    public partial class MainWindow
    {
        private void BtnInstallFromFile_Click(object sender, RoutedEventArgs e)
        {
            if (_isUiTestMode)
            {
                AddLog("UI test: установка из локального файла");
                return;
            }

            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Архив клиента Ven4Tools (*.zip)|*.zip",
                Title = "Выберите архив клиента"
            };
            if (dialog.ShowDialog() != true) return;

            _downloadCts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            _ = InstallFromLocalArchiveAsync(dialog.FileName, _downloadCts.Token, silent: false);
        }

        internal async Task InstallFromLocalArchiveAsync(string archivePath, CancellationToken token, bool silent)
        {
            Dispatcher.Invoke(() =>
            {
                progressDownload.Value = 0;
                txtDownloadStatus.Text = "Проверка подписи...";
                btnCancelDownload.Visibility = silent ? Visibility.Collapsed : Visibility.Visible;
                btnLaunchApp.IsEnabled = false;
            });
            SetOperationStage(2); // Проверка целостности

            try
            {
                AddLog($"📂 Установка из локального файла: {archivePath}");
                using var cdnService = new CdnService();
                var result = await LocalArchiveVerifier.VerifyAsync(archivePath, cdnService, token);

                if (result.Outcome == LocalArchiveOutcome.Rejected)
                {
                    Dispatcher.Invoke(() => txtDownloadStatus.Text = "Отклонено");
                    SetOperationStage(0);
                    AddLog($"⛔ {result.RejectionReason}");
                    if (!silent)
                        Dispatcher.Invoke(() => System.Windows.MessageBox.Show(
                            result.RejectionReason, "Установка отклонена",
                            MessageBoxButton.OK, MessageBoxImage.Error));
                    return;
                }

                AddLog(result.Outcome == LocalArchiveOutcome.Offline
                    ? $"✅ Офлайн-подпись подтверждена (версия {result.Version})"
                    : $"✅ Подтверждено по списку исторических версий (версия {result.Version})");

                if (result.Outcome == LocalArchiveOutcome.Historical)
                {
                    string warning =
                        $"Это архивная версия {result.Version} — подтверждена по списку ранее опубликованных " +
                        "версий, но не имеет встроенной подписи.\n\nРекомендуем скачать актуальную версию через " +
                        "обычную загрузку.\n\nВсё равно установить архивную версию?";
                    AddLog($"⚠️ Архивная версия {result.Version} без встроенной подписи, подтверждена по сети");

                    if (!silent)
                    {
                        var answer = Dispatcher.Invoke(() => System.Windows.MessageBox.Show(
                            warning, "Архивная версия", MessageBoxButton.YesNo, MessageBoxImage.Warning));
                        if (answer != MessageBoxResult.Yes)
                        {
                            Dispatcher.Invoke(() => txtDownloadStatus.Text = "Отменено");
                            SetOperationStage(0);
                            AddLog("⏹ Установка архивной версии отменена пользователем");
                            return;
                        }
                    }
                }

                bool installed = await ExtractAndInstallClientAsync(archivePath, result.Version ?? "?", token, silent);
                if (installed && !silent)
                    Dispatcher.Invoke(() => System.Windows.MessageBox.Show(
                        $"Клиент {result.Version} успешно установлен в:\n{_clientPath}",
                        "Установка завершена", MessageBoxButton.OK, MessageBoxImage.Information));
            }
            catch (OperationCanceledException)
            {
                Dispatcher.Invoke(() => { txtDownloadStatus.Text = "Отменено"; progressDownload.Value = 0; });
                SetOperationStage(0);
                AddLog("⏹ Установка из файла отменена");
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => txtDownloadStatus.Text = "Ошибка");
                SetOperationStage(0);
                AddLog($"❌ Ошибка установки из файла: {ex.Message}");
                if (!silent)
                    Dispatcher.Invoke(() => System.Windows.MessageBox.Show(
                        $"Ошибка: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error));
            }
            finally
            {
                Dispatcher.Invoke(() =>
                {
                    btnCancelDownload.Visibility = Visibility.Collapsed;
                    btnCancelDownload.IsEnabled = true;
                    btnLaunchApp.IsEnabled = true;
                });
                _downloadCts?.Dispose();
                _downloadCts = null;
            }
        }
    }
}
```

`Dispatcher.Invoke` вокруг UI-обращений — метод помечен `internal` и вызывается также из `CliInstallRunner` (Task 7) не с UI-потока; остальной код `MainWindow` (например, `DownloadVersionAsync`) исторически обращается к UI-элементам без `Dispatcher.Invoke`, потому что раньше вызывался только из UI-потока (обработчик кнопки) — здесь другой случай, метод асинхронно доступен и из headless CLI-пути, поэтому обращения к элементам обёрнуты явно.

- [ ] **Step 6: Собрать, проверить, что кнопка появляется**

Run: `dotnet build Ven4Tools.sln -c Release` — 0 ошибок, 0 предупреждений.
Запустить `Ven4Tools.Launcher.exe` вручную, убедиться, что кнопка «Установить из файла...» видна под «Установить Ven4Tools», диалог выбора файла открывается по клику.

- [ ] **Step 7: Commit**

```bash
git add Ven4Tools.Launcher/MainWindow.Download.cs Ven4Tools.Launcher/MainWindow.Download.LocalArchive.cs Ven4Tools.Launcher/MainWindow.xaml Ven4Tools.Launcher/Services/LauncherPaths.cs
git commit -m "Лаунчер: кнопка «Установить из файла» + общий путь распаковки/установки"
```

---

### Task 7: CLI `--install-from=<path> [--silent]`

**Files:**
- Create: `Ven4Tools.Launcher/CliInstallRunner.cs`
- Modify: `Ven4Tools.Launcher/App.xaml.cs`

**Interfaces:**
- Consumes: `InstallFromLocalArchiveAsync` (Task 6, теперь `internal async Task<bool>`, вызывается на headless `MainWindow`-инстансе).
- Produces: `internal static class CliInstallRunner { public static Task<int> RunAsync(MainWindow window, string archivePath, bool silent); }` — код возврата процесса: `0` успех, `1` отказ верификации/установки/исключение, `3` уже запущен другой экземпляр. (`LauncherPaths.ResolveClientPath()` из Task 6 в итоге этой задачей не используется — `_clientPath` полностью резолвится конструктором `MainWindow` без обращения к нему.)

**Важное архитектурное решение (ПЕРЕСМОТРЕНО при исполнении — исходный вариант плана реально зависает, см. ниже):** headless-путь переиспользует САМ класс `MainWindow` (создаёт экземпляр без `Show()`) — это даёт доступ ко всем уже существующим полям и методам (`_clientPath`, `AddLog`, `SetOperationStage`, `ExtractAndInstallClientAsync`) без дублирования логики установки в отдельном классе.

**Найденный при исполнении критический баг исходного текста плана (не гипотеза — воспроизведено эмпирически имплементором Task 7, зависание на 100% запусков):** исходная версия этого шага предлагала `window.InstallFromLocalArchiveAsync(...).GetAwaiter().GetResult()` синхронно внутри `App.OnStartup`. Это гарантированно вешает процесс намертво на самом первом `Dispatcher.Invoke` внутри `InstallFromLocalArchiveAsync` — ещё до `AddLog`, до любого `await`. Причина: `Dispatcher.Invoke` требует запущенного消息-цикла (`Dispatcher.Run()`), а он стартует только ПОСЛЕ возврата из `OnStartup` (WPF: `Application.Run()` вызывает `OnStartup`, и только потом — `Dispatcher.Run()`). CLI-ветка блокирует именно тот поток, который должен этот цикл запустить — классический deadlock, не редкая гонка, воспроизводится каждый раз.

Также попутно найдено (и здесь же исправляется): исходный текст плана жёстко подставлял `silent: true` в вызов `InstallFromLocalArchiveAsync`, независимо от фактического CLI-флага `--silent` — делая собственный флаг `--silent` мёртвым параметром. Это противоречило же описанию в спеке («без флага `--silent` возможны диалоги») — исправлено ниже, флаг прокидывается по назначению.

**Исправленная архитектура:** не блокировать поток внутри `OnStartup` синхронно. Вместо `.GetAwaiter().GetResult()` — поставить установку в очередь диспетчера через `Dispatcher.BeginInvoke` и вернуться из `OnStartup` немедленно, дав `Application.Run()` запустить `Dispatcher.Run()`. К моменту, когда очередь дойдёт до нашего колбэка, цикл уже активен — `Dispatcher.Invoke` внутри `InstallFromLocalArchiveAsync` работает штатно. `ShutdownMode` явно ставится в `OnExplicitShutdown` — иначе поведение WPF при нуле открытых окон (`OnLastWindowClose` по умолчанию) для этого пути не гарантировано и не проверялось.

- [ ] **Step 1: Реализовать `CliInstallRunner`**

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Ven4Tools.Launcher;

/// <summary>
/// Headless-путь для `Ven4Tools.Launcher.exe --install-from=<path> [--silent]` —
/// скриптовое/автоматизированное разворачивание клиента без открытия окна лаунчера.
/// Переиспользует MainWindow (без Show()) вместо дублирования логики установки —
/// InstallFromLocalArchiveAsync и ExtractAndInstallClientAsync общие с обычным UI-путём.
/// Вызывается ТОЛЬКО после того, как Dispatcher уже запущен (через Dispatcher.BeginInvoke
/// из App.OnStartup, см. Step 3) — синхронный вызов до старта цикла диспетчера
/// гарантированно вешает процесс на первом же Dispatcher.Invoke внутри
/// InstallFromLocalArchiveAsync (эмпирически воспроизведено при исполнении Task 7).
/// </summary>
internal static class CliInstallRunner
{
    public static async Task<int> RunAsync(MainWindow window, string archivePath, bool silent)
    {
        try
        {
            bool success = await window.InstallFromLocalArchiveAsync(
                archivePath, CancellationToken.None, silent);
            return success ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Ошибка: {ex.Message}");
            return 1;
        }
    }
}
```

**Пересмотр интерфейса Task 6:** в Task 6 `InstallFromLocalArchiveAsync` меняет возвращаемый тип с `Task` на `Task<bool>` (`true` — успех), и всюду, где он вызывался как `_ = InstallFromLocalArchiveAsync(...)` (fire-and-forget из `BtnInstallFromFile_Click`), это не ломается — `Task<bool>`, отброшенный как `_`, работает точно так же. Обновить сигнатуру в Task 6, Step 5 (`internal async Task<bool> InstallFromLocalArchiveAsync(...)`), и все `return;` внутри неё на `return false;`, а последний успешный путь — на `return installed;`.

- [ ] **Step 2: Проверить `AddLog` и side-эффекты конструктора `MainWindow`**

Run: `grep -n "private void AddLog" Ven4Tools.Launcher/MainWindow.Tray.cs`

`AddLog` уже использует `Dispatcher.Invoke` внутри себя (пишет только в UI-текстбокс `txtLog`, файлового лога у лаунчера сейчас нет вообще — это факт, а не решение этой задачи; если нужен файловый след CLI-запуска, это отдельная будущая доработка, не блокирует эту задачу). Отдельно зафиксировать (без исправления в рамках этой задачи — вне её объёма): конструктор `MainWindow` безусловно вызывает `CreateTrayIcon()` и `StartBackgroundService()`, даже когда окно никогда не показывается — при `--install-from --silent` это значит, что иконка в трее и фоновый апдейтер могут на короткое время появиться/запуститься до `Shutdown()`. Не устраняется в этой задаче (потребовало бы отдельного headless-флага в конструктор `MainWindow` — более широкое изменение, чем предполагает эта задача); зафиксировать как известное ограничение в комментарии `CliInstallRunner` или в логе плана.

- [ ] **Step 3: Разбор аргументов и запуск через `Dispatcher.BeginInvoke` в `App.xaml.cs`**

В `Ven4Tools.Launcher/App.xaml.cs`, метод `OnStartup`, сразу после блока `VEN4TOOLS_UI_TEST` (после строки `if (...) { base.OnStartup(e); return; }`) и до создания обычного single-instance мьютекса:

```csharp
        string? installFromPath = null;
        bool silentInstall = false;
        foreach (var arg in e.Args)
        {
            if (arg.StartsWith("--install-from=", StringComparison.OrdinalIgnoreCase))
                installFromPath = arg["--install-from=".Length..].Trim('"');
            else if (string.Equals(arg, "--silent", StringComparison.OrdinalIgnoreCase))
                silentInstall = true;
        }

        if (installFromPath != null)
        {
            _mutex = new Mutex(true, "Ven4Tools.Launcher.SingleInstance", out bool createdNewCli);
            if (!createdNewCli)
            {
                Console.Error.WriteLine("Ven4Tools Launcher уже запущен.");
                _mutex.Dispose();
                _mutex = null;
                Shutdown(3);
                return;
            }

            // Явный режим завершения: при нуле показанных окон поведение WPF-режима
            // по умолчанию (OnLastWindowClose) для этого пути не проверялось —
            // завершаем процесс сами, точным кодом возврата, без зависимости от
            // подсчёта окон.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var window = new MainWindow();

            // КРИТИЧНО: не блокировать здесь синхронно (.GetAwaiter().GetResult()) —
            // Dispatcher.Run() запускается только ПОСЛЕ возврата из OnStartup, а
            // InstallFromLocalArchiveAsync использует Dispatcher.Invoke, которому
            // для работы нужен уже запущенный цикл диспетчера. Синхронная блокировка
            // здесь = гарантированный deadlock (воспроизведено эмпирически при
            // исполнении этой задачи). BeginInvoke ставит колбэк в очередь и
            // возвращается немедленно — OnStartup завершается, Application.Run()
            // запускает Dispatcher.Run(), и только тогда колбэк реально выполняется.
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                int exitCode = await CliInstallRunner.RunAsync(window, installFromPath, silentInstall);
                ReleaseSingleInstanceMutex();
                Shutdown(exitCode);
            }));
            return;
        }
```

- [ ] **Step 4: Собрать, проверить вручную**

Run: `dotnet build Ven4Tools.sln -c Release` — 0 ошибок, 0 предупреждений.
Run (вручную, с любым zip-файлом — реальный сигнал успеха здесь не «клиент установился», а «процесс реально завершается с кодом возврата, а не висит»):
```powershell
.\Ven4Tools.Launcher\bin\Release\net8.0-windows\win-x64\Ven4Tools.Launcher.exe --install-from="C:\путь\к\любому.zip" --silent
echo $LASTEXITCODE
```
Expected: процесс завершается за разумное время (не висит бесконечно) с каким-то кодом возврата (0 при реально валидном подписанном архиве из релиза, 1 при отказе верификации на случайном zip — оба варианта доказывают, что deadlock устранён, окно лаунчера не появлялось). Если процесс всё ещё висит — это блокирующая находка, не коммитить, эскалировать.

- [ ] **Step 5: Commit**

```bash
git add Ven4Tools.Launcher/CliInstallRunner.cs Ven4Tools.Launcher/App.xaml.cs Ven4Tools.Launcher/MainWindow.Download.LocalArchive.cs
git commit -m "Лаунчер: CLI --install-from=<path> [--silent] для скриптового разворачивания"
```

---

### Task 8: UI-тест — кнопка «Установить из файла» в существующем smoke-наборе

**Files:**
- Modify: `tests/Ven4Tools.UITests/LauncherSmokeTests.cs`
- Modify: `tests/Ven4Tools.UITests/Snapshots/launcher-main.png` (перегенерировать)

**Примечание, уточняющее раздел «Тестирование» спеки:** спека описывала «UI-тест happy path... полный путь от выбора файла до завершённой установки». По факту изучения `LauncherSmokeTests.cs` — ВСЕ существующие кнопки, включая саму `btnLaunchApp` («Установить Ven4Tools»), в `VEN4TOOLS_UI_TEST=1` режиме не выполняют реальную установку (`if (_isUiTestMode) { AddLog(...); return; }` в каждом обработчике) — реального сетевого/файлового E2E через UIA в этом наборе нет ни для одной кнопки, только: элемент существует, имеет `AutomationId`, включён, не роняет процесс по клику. Новая кнопка проверяется на ОБЩИХ основаниях с остальными, без специальной инфраструктуры для «настоящей» установки — это соответствует фактическому масштабу существующего smoke-набора, а не расхождение с ним.

**Interfaces:**
- Consumes: `btnInstallFromFile` (Task 6, Step 4 — `AutomationId` в WPF берётся из `x:Name` автоматически).

- [ ] **Step 1: Добавить `"btnInstallFromFile"` в три места `LauncherSmokeTests.cs`**

В массив `requiredEnabledControls` (метод `AssertPrimaryControlsAreAvailable`):
```csharp
        string[] requiredEnabledControls =
        [
            "btnSelectFolder",
            "btnFindClient",
            "btnCheckUpdates",
            "btnLaunchApp",
            "btnInstallFromFile",
            "btnChangelog",
            "btnOpenSettings",
            "btnDeleteClient",
            "btnExit"
        ];
```

В массив `functionalButtons` (метод `FunctionalButtonsExposeExplanations`):
```csharp
        string[] functionalButtons =
        [
            "btnSelectFolder",
            "btnFindClient",
            "btnCheckUpdates",
            "btnLaunchApp",
            "btnInstallFromFile",
            "btnChangelog",
            "btnOpenSettings",
            "btnDeleteClient"
        ];
```

В список кликов метода `ExercisePrimaryControlBindings`:
```csharp
        foreach (string automationId in new[]
        {
            "btnSelectFolder",
            "btnFindClient",
            "btnCheckUpdates",
            "btnLaunchApp",
            "btnInstallFromFile",
            "btnDeleteClient"
        })
```

- [ ] **Step 2: Собрать Release, прогнать UI-тесты**

Run:
```powershell
dotnet build Ven4Tools.sln -c Release
dotnet test tests/Ven4Tools.UITests -c Release --filter "FullyQualifiedName~FunctionalButtonsExposeExplanations"
```
Expected: PASS — новая кнопка имеет непустой `ToolTip`/`HelpText` (уже задан в Task 6, Step 4), тест находит и её.

- [ ] **Step 3: Перегенерировать снапшот-баселайн**

Добавление видимой кнопки меняет layout главного окна — тест `MainWindow_IsVisibleUsableAndMatchesSnapshot` сравнивает пиксели с сохранённым `launcher-main.png` и упадёт без обновления эталона.

Run:
```powershell
$env:UPDATE_SNAPSHOTS = "1"
dotnet test tests/Ven4Tools.UITests -c Release --filter "FullyQualifiedName~MainWindow_IsVisibleUsableAndMatchesSnapshot"
Remove-Item Env:\UPDATE_SNAPSHOTS
dotnet test tests/Ven4Tools.UITests -c Release --filter "FullyQualifiedName~MainWindow_IsVisibleUsableAndMatchesSnapshot"
```
Expected: первый прогон обновляет `tests/Ven4Tools.UITests/Snapshots/launcher-main.png` и проходит (сравнение с самим собой); второй (без `UPDATE_SNAPSHOTS`) — контрольный прогон, тоже PASS.

- [ ] **Step 4: Commit**

```bash
git add tests/Ven4Tools.UITests/LauncherSmokeTests.cs tests/Ven4Tools.UITests/Snapshots/launcher-main.png
git commit -m "UI-тесты лаунчера: покрыть кнопку «Установить из файла», обновить снапшот"
```

---

## Self-Review

**1. Покрытие спеки:**
- «Два независимых хеша» (H_canonical + revocation) → Task 1, Task 5. ✅
- `Tools/ClientArchiveSigner` + встроенный ключ → Task 3, Task 4. ✅ (с явным, обоснованным отклонением: version включена в подписываемый payload).
- `revokedClientHashes` в version.json → Task 2, Task 5 (`IsRevoked`). ✅
- `historicalClientArchives` (правка спеки от 2026-08-02) → Task 2, Task 5 (сетевая ветка). ✅
- `LocalArchiveVerifier` поток проверки (офлайн → исторический список → отзыв) → Task 5. ✅
- UI-вход + предупреждение для архивных версий → Task 6. ✅
- CLI `--install-from=<path> [--silent]` → Task 7. ✅
- Единый путь распаковки/установки с сетевым путём (не плодить два места) → Task 6, Step 1 (рефакторинг `DownloadVersionAsync`). ✅
- Тестирование (юнит + UI) → Task 1, 2, 4, 5 (юнит), Task 8 (UI, со скорректированным по факту масштабом). ✅
- «Не входит в объём»: Setup.exe лаунчера, изменения сетевого пути — ничего из этого не тронуто ни в одной задаче. ✅

**2. Плейсхолдеры:** единственное намеренное место подстановки по факту выполнения — константа `PublicKey` в Task 4, Step 5 (значение физически не существует до генерации ключа в Task 3, Step 1) — явно помечено и объяснено, не является "TBD"-заглушкой в смысле правила.

**3. Согласованность типов:** `LocalArchiveVerificationResult`/`LocalArchiveOutcome` (Task 5) используются с одинаковыми именами полей (`Outcome`, `Version`, `RejectionReason`) в Task 6 и нигде не переименовываются. `InstallFromLocalArchiveAsync` — сигнатура пересмотрена в Task 7 (`Task` → `Task<bool>`) с явным указанием вернуться и поправить Task 6, Step 5 — это сделано внутри плана до его исполнения, а не оставлено как несостыковка.
