using System;
using System.Collections.Generic;
using System.Linq;
using Ven4Tools.Launcher.Models;

namespace Ven4Tools.Launcher.Services;

/// <summary>
/// Отзыв архивов клиента по подписанному version.json. Архив отозван, если совпал
/// хоть один признак: SHA256 файла (revokedClientHashes), канонический хеш
/// содержимого (revokedClientCanonicalHashes) или версия (revokedClientVersions).
///
/// Раньше сверялся только SHA256 файла, а подпись архива стоит на каноническом
/// хеше содержимого: тот же подписанный архив, пересжатый заново, получал другой
/// SHA256 файла и проходил мимо отзыва. Канонический хеш и версия от пересжатия не
/// зависят. Чистые функции — покрыты unit-тестами.
/// </summary>
internal static class ClientRevocation
{
    /// <summary>Текст отказа — один для всех признаков отзыва, как было у отзыва по хешу файла.</summary>
    internal const string RejectionMessage =
        "Эта версия отозвана — скачайте актуальную через обычную загрузку.";

    /// <summary>
    /// Отозван ли архив. Любой аргумент может быть null — тогда этот признак не
    /// проверяется. Манифест null (сеть недоступна) — «не отозван»: отзыв best-effort,
    /// как и раньше.
    /// </summary>
    internal static bool IsRevoked(
        CdnVersionInfo? info, string? wholeFileSha256, string? canonicalHashHex, string? version)
    {
        if (info == null) return false;
        return ContainsHash(info.RevokedClientHashes, wholeFileSha256) ||
               ContainsHash(info.RevokedClientCanonicalHashes, canonicalHashHex) ||
               IsVersionRevoked(info, version);
    }

    /// <summary>Отозвана ли версия клиента (VersionComparer: «6.1.0» = «6.1.0.0»).</summary>
    internal static bool IsVersionRevoked(CdnVersionInfo? info, string? version)
    {
        if (string.IsNullOrWhiteSpace(version) || info?.RevokedClientVersions == null) return false;
        return info.RevokedClientVersions.Any(v =>
            !string.IsNullOrWhiteSpace(v) && VersionComparer.Compare(v.Trim(), version.Trim()) == 0);
    }

    private static bool ContainsHash(List<string>? list, string? hash)
    {
        if (string.IsNullOrWhiteSpace(hash) || list == null) return false;
        return list.Any(h => h != null && string.Equals(h.Trim(), hash.Trim(), StringComparison.OrdinalIgnoreCase));
    }
}
