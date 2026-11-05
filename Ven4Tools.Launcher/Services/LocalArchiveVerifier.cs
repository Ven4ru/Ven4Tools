using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
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

    /// <summary>
    /// Текущая опубликованная версия клиента из подписанного version.json — если он
    /// был получен при этой проверке (для офлайн-подписи сеть необязательна, тогда
    /// null). Сама проверка от неё не зависит: подписанный архив старой версии
    /// остаётся подлинным. Нужна вызывающему коду, чтобы предупредить о понижении
    /// версии — см. <see cref="ClientDowngradePolicy"/>.
    /// </summary>
    public string? PublishedClientVersion { get; init; }

    public static LocalArchiveVerificationResult Reject(string reason) =>
        new() { Outcome = LocalArchiveOutcome.Rejected, RejectionReason = reason };
}

/// <summary>
/// Проверка локального архива клиента перед установкой: сначала встроенная
/// офлайн-подпись (CanonicalArchiveHasher + ClientArchiveVerifier), при её
/// отсутствии — обязательная сетевая сверка с historicalClientArchives
/// (архивы, выпущенные до появления офлайн-подписи). В обоих случаях —
/// best-effort сверка со списками отзыва (хеш файла, канонический хеш содержимого,
/// версия — см. <see cref="ClientRevocation"/>).
/// </summary>
internal static class LocalArchiveVerifier
{
    public static Task<LocalArchiveVerificationResult> VerifyAsync(
        string archivePath, CdnService cdnService, CancellationToken token) =>
        VerifyAsync(archivePath, t => TryGetVersionInfoAsync(cdnService, t), token);

    /// <summary>
    /// То же с подменяемым источником подписанного манифеста — для тестов списков
    /// отзыва (CdnService не абстрагирован, а подписать манифест в тесте нечем).
    /// </summary>
    internal static async Task<LocalArchiveVerificationResult> VerifyAsync(
        string archivePath, Func<CancellationToken, Task<CdnVersionInfo?>> getVersionInfo, CancellationToken token)
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
            var info = await getVersionInfo(token);
            if (ClientRevocation.IsRevoked(info, wholeFileSha256, canonicalHashHex, signatureFile!.Version))
                return LocalArchiveVerificationResult.Reject(ClientRevocation.RejectionMessage);

            return new LocalArchiveVerificationResult
            {
                Outcome = LocalArchiveOutcome.Offline,
                Version = signatureFile!.Version,
                PublishedClientVersion = info?.Client?.Version
            };
        }

        // Нет валидной офлайн-подписи — сеть здесь ОБЯЗАТЕЛЬНА (единственный
        // источник доверия для архивов без встроенной подписи).
        var networkInfo = await getVersionInfo(token);
        if (networkInfo == null)
            return LocalArchiveVerificationResult.Reject(
                "Архив без офлайн-подписи, а сеть для проверки исторического списка версий недоступна — установка отменена.");

        // Версия архива без офлайн-подписи известна только из исторического списка —
        // поэтому отзыв по версии проверяется после сопоставления с ним.
        if (ClientRevocation.IsRevoked(networkInfo, wholeFileSha256, canonicalHashHex, version: null))
            return LocalArchiveVerificationResult.Reject(ClientRevocation.RejectionMessage);

        var historicalMatch = networkInfo.HistoricalClientArchives?.FirstOrDefault(h =>
            string.Equals(h.Sha256, wholeFileSha256, StringComparison.OrdinalIgnoreCase));
        if (historicalMatch == null)
            return LocalArchiveVerificationResult.Reject(
                "Архив без офлайн-подписи и не значится в списке ранее опубликованных версий — установка отменена.");

        if (ClientRevocation.IsVersionRevoked(networkInfo, historicalMatch.Version))
            return LocalArchiveVerificationResult.Reject(ClientRevocation.RejectionMessage);

        return new LocalArchiveVerificationResult
        {
            Outcome = LocalArchiveOutcome.Historical,
            Version = historicalMatch.Version,
            PublishedClientVersion = networkInfo.Client?.Version
        };
    }

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

    private static Task<string> ComputeWholeFileSha256Async(string path, CancellationToken token) =>
        Helpers.FileHashHelper.ComputeSha256Async(path, token);
}
