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
