using System.Text;
using Ven4Tools.Services;

namespace Ven4Tools.Tests;

/// <summary>
/// Декодирование скачанного каталога и его подписи.
///
/// Тот же класс дефекта, что убил CDN в лаунчере (см. <see cref="BoundedHttpTextTests"/>):
/// <c>CatalogLoaderService</c> читает поток вручную ради предела размера ДО буферизации,
/// и декодировал байты через <c>Encoding.UTF8.GetString</c>, который оставляет BOM
/// первым символом строки. ECDSA-подпись каталога считается по содержимому БЕЗ BOM,
/// поэтому master.json, выложенный с BOM, не прошёл бы проверку ни на одном из трёх
/// источников — клиент отверг бы каталог целиком и показал «каталог недоступен».
///
/// На момент правки master.json BOM ещё не получил (проверено на всех источниках) —
/// это единственная причина, по которой каталог продолжал работать. version.json на
/// CDN его уже имел, а выкладываются они одними и теми же средствами.
/// </summary>
public sealed class CatalogDownloadDecodingTests
{
    private const string Payload = "{\n  \"version\": 19,\n  \"apps\": []\n}";

    [Fact]
    public void DecodeDownloadedText_StripsUtf8Bom()
    {
        byte[] withBom = Encoding.UTF8.GetPreamble()
            .Concat(Encoding.UTF8.GetBytes(Payload))
            .ToArray();

        Assert.Equal(Payload, CatalogLoaderService.DecodeDownloadedText(withBom));
    }

    [Fact]
    public void DecodeDownloadedText_KeepsContentWithoutBomIntact()
    {
        Assert.Equal(
            Payload,
            CatalogLoaderService.DecodeDownloadedText(Encoding.UTF8.GetBytes(Payload)));
    }

    /// <summary>
    /// Подпись каталога уходит в Convert.FromBase64String: лишний символ в начале
    /// строки даёт FormatException, а не честное «подпись не совпала».
    /// </summary>
    [Fact]
    public void DecodeDownloadedText_SignatureStaysValidBase64()
    {
        const string signature = "AGyR7pXF6yO0QlyoQuzgcLa1x9f7+qkmsPoI9OsJYlB3GnSFPHzfiySm3qpzID1EtbpRaSOrma6AF4Wf7uw6hA==";
        byte[] withBom = Encoding.UTF8.GetPreamble()
            .Concat(Encoding.UTF8.GetBytes(signature))
            .ToArray();

        string decoded = CatalogLoaderService.DecodeDownloadedText(withBom);

        Assert.Equal(signature, decoded);
        _ = Convert.FromBase64String(decoded); // не должно бросить
    }

    [Fact]
    public void DecodeDownloadedText_HandlesEmptyResponse()
    {
        Assert.Equal(string.Empty, CatalogLoaderService.DecodeDownloadedText(Array.Empty<byte>()));
    }

    /// <summary>
    /// Кириллица в названиях и описаниях приложений — половина каталога. Убедиться,
    /// что смена способа декодирования её не испортила.
    /// </summary>
    [Fact]
    public void DecodeDownloadedText_PreservesCyrillic()
    {
        const string text = "{\"name\":\"Яндекс Браузер\",\"desc\":\"Быстрый браузер — с защитой\"}";
        byte[] withBom = Encoding.UTF8.GetPreamble()
            .Concat(Encoding.UTF8.GetBytes(text))
            .ToArray();

        Assert.Equal(text, CatalogLoaderService.DecodeDownloadedText(withBom));
    }
}
