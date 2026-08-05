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
