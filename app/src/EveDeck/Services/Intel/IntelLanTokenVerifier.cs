using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EveDeck.Services.Intel;

public static class IntelLanTokenVerifier
{
    public const string Audience = "evedeck-intel-lan";
    private const string PublicKeySpkiDerBase64 = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEI7do49gY/YQV3JSBduylSBEz3KMonogq6Q0JZG8NGD66JWitjSHmEKKqcZciQmBkowNHLv3Tsp1J9EvZFZsJ7w==";
    private static readonly byte[] PublicKeySpkiDer = Convert.FromBase64String(PublicKeySpkiDerBase64);
    private static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(60);
    private static readonly IntelLanTokenPayload EmptyPayload = new(0, "", 0, "", 0, 0);

    public static bool TryVerify(string? token, IReadOnlySet<long> allowedCharacterIds, DateTimeOffset now, out IntelLanTokenPayload payload)
        => TryVerify(token, allowedCharacterIds, now, PublicKeySpkiDer, out payload);

    internal static bool TryVerify(
        string? token,
        IReadOnlySet<long> allowedCharacterIds,
        DateTimeOffset now,
        ReadOnlySpan<byte> publicKeySpkiDer,
        out IntelLanTokenPayload payload)
    {
        payload = EmptyPayload;

        try
        {
            if (string.IsNullOrWhiteSpace(token) || allowedCharacterIds.Count == 0) return false;

            var parts = token.Split('.');
            if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0) return false;

            var data = Encoding.ASCII.GetBytes(parts[0]);
            var signature = Base64UrlDecode(parts[1]);
            if (signature.Length != 64) return false;

            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(publicKeySpkiDer, out _);
            if (!ecdsa.VerifyData(
                    data,
                    signature,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            {
                return false;
            }

            var payloadJson = Base64UrlDecode(parts[0]);
            var parsed = JsonSerializer.Deserialize<IntelLanTokenPayload>(payloadJson);
            if (parsed is null) return false;

            var nowUnix = now.ToUnixTimeSeconds();
            if (parsed.V != 1) return false;
            if (!string.Equals(parsed.Aud, Audience, StringComparison.Ordinal)) return false;
            if (!allowedCharacterIds.Contains(parsed.Cid)) return false;
            if (parsed.Exp <= nowUnix - (long)ClockSkew.TotalSeconds) return false;
            if (parsed.Iat > nowUnix + (long)ClockSkew.TotalSeconds) return false;

            payload = parsed;
            return true;
        }
        catch
        {
            payload = EmptyPayload;
            return false;
        }
    }

    private static byte[] Base64UrlDecode(string text)
    {
        var normalized = text.Replace('-', '+').Replace('_', '/');
        normalized = normalized.PadRight(normalized.Length + (4 - normalized.Length % 4) % 4, '=');
        return Convert.FromBase64String(normalized);
    }
}

public sealed record IntelLanTokenPayload(
    [property: JsonPropertyName("v")]
    int V,
    [property: JsonPropertyName("aud")]
    string Aud,
    [property: JsonPropertyName("cid")]
    long Cid,
    [property: JsonPropertyName("name")]
    string Name,
    [property: JsonPropertyName("iat")]
    long Iat,
    [property: JsonPropertyName("exp")]
    long Exp);
