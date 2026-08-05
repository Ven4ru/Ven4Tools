using System.IO.Compression;
using System.Text.Json;
using Ven4Tools.Launcher.Models;
using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Tests;

public sealed class LocalArchiveVerifierTests
{
    private static string CopyFixture(string fileName)
    {
        string source = Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
        string dest = Path.Combine(Path.GetTempPath(), $"lavtest_{Guid.NewGuid():N}.zip");
        File.Copy(source, dest);
        return dest;
    }

    // Портит именно значение "signature" — единственное поле, которое реально
    // участвует в криптопроверке (ClientArchiveVerifier.Verify пересчитывает
    // канонический хеш заново из содержимого архива и не доверяет полю
    // sha256_canonical из JSON, см. ClientArchiveVerifierTests — оно там нигде
    // не читается). Тампер поля sha256_canonical (как в черновике Step 1
    // брифа) — не-операция для этой (устойчивой к подмене вспомогательного
    // поля) схемы подписи и не проверяет то, что заявлено в названии теста.
    private static void FlipByteInSignatureEntry(string zipPath)
    {
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Update);
        var entry = archive.GetEntry(CanonicalArchiveHasher.SignatureEntryName)!;
        string json;
        using (var reader = new StreamReader(entry.Open())) json = reader.ReadToEnd();
        var sig = JsonSerializer.Deserialize<ClientArchiveSignatureFile>(json)!;
        char[] chars = sig.Signature!.ToCharArray();
        chars[0] = chars[0] == 'A' ? 'B' : 'A';
        sig.Signature = new string(chars);
        entry.Delete();
        var newEntry = archive.CreateEntry(CanonicalArchiveHasher.SignatureEntryName);
        using var writer = new StreamWriter(newEntry.Open());
        writer.Write(JsonSerializer.Serialize(sig));
    }

    // CdnService не абстрагирован интерфейсом — тесты, которым сеть не нужна
    // (валидная офлайн-подпись без проверки отзыва, и «сети нет» для проверки
    // архива без офлайн-подписи), передают null и полагаются на то, что
    // LocalArchiveVerifier трактует null как «сеть недоступна»
    // (TryGetVersionInfoAsync возвращает null без обращения к cdnService).
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
            var result = await LocalArchiveVerifier.VerifyAsync(archive, cdnService: null!, CancellationToken.None);
            Assert.Equal(LocalArchiveOutcome.Rejected, result.Outcome);
            Assert.Contains("сеть", result.RejectionReason, StringComparison.OrdinalIgnoreCase);
        }
        finally { File.Delete(archive); }
    }
}
