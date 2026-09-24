using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EveDeck.Services.Intel;
using Xunit;

namespace EveDeck.Tests;

public class IntelLanTokenVerifierTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    [Fact]
    public void ValidToken_WithAllowedCharacter_IsAccepted()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var token = Sign(key, new Payload(1, IntelLanTokenVerifier.Audience, 90000001, "Test Pilot", Now.ToUnixTimeSeconds(), Now.AddDays(1).ToUnixTimeSeconds()));

        var ok = IntelLanTokenVerifier.TryVerify(
            token,
            new HashSet<long> { 90000001 },
            Now,
            key.ExportSubjectPublicKeyInfo(),
            out var payload);

        Assert.True(ok);
        Assert.Equal(90000001, payload.Cid);
        Assert.Equal("Test Pilot", payload.Name);
    }

    [Fact]
    public void ValidSignature_WithUnlinkedCharacter_IsRejected()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var token = Sign(key, new Payload(1, IntelLanTokenVerifier.Audience, 90000002, "Test Pilot", Now.ToUnixTimeSeconds(), Now.AddDays(1).ToUnixTimeSeconds()));

        Assert.False(IntelLanTokenVerifier.TryVerify(
            token,
            new HashSet<long> { 90000001 },
            Now,
            key.ExportSubjectPublicKeyInfo(),
            out _));
    }

    [Fact]
    public void EmptyAllowedList_RejectsEverything()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var token = Sign(key, new Payload(1, IntelLanTokenVerifier.Audience, 90000001, "Test Pilot", Now.ToUnixTimeSeconds(), Now.AddDays(1).ToUnixTimeSeconds()));

        Assert.False(IntelLanTokenVerifier.TryVerify(
            token,
            new HashSet<long>(),
            Now,
            key.ExportSubjectPublicKeyInfo(),
            out _));
    }

    [Theory]
    [InlineData(2, IntelLanTokenVerifier.Audience, 90000001, 0, 86400)]
    [InlineData(1, "other-audience", 90000001, 0, 86400)]
    [InlineData(1, IntelLanTokenVerifier.Audience, 90000001, 120, 86400)]
    [InlineData(1, IntelLanTokenVerifier.Audience, 90000001, 0, -61)]
    public void InvalidClaims_AreRejected(int version, string audience, long characterId, int iatOffsetSeconds, int expOffsetSeconds)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var token = Sign(key, new Payload(
            version,
            audience,
            characterId,
            "Test Pilot",
            Now.AddSeconds(iatOffsetSeconds).ToUnixTimeSeconds(),
            Now.AddSeconds(expOffsetSeconds).ToUnixTimeSeconds()));

        Assert.False(IntelLanTokenVerifier.TryVerify(
            token,
            new HashSet<long> { 90000001 },
            Now,
            key.ExportSubjectPublicKeyInfo(),
            out _));
    }

    [Fact]
    public void MalformedInput_DoesNotThrow()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        Assert.False(IntelLanTokenVerifier.TryVerify(
            "not.a.valid.token",
            new HashSet<long> { 90000001 },
            Now,
            key.ExportSubjectPublicKeyInfo(),
            out _));
    }

    private static string Sign(ECDsa key, Payload payload)
    {
        var payloadSegment = Base64Url(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)));
        var signature = key.SignData(
            Encoding.ASCII.GetBytes(payloadSegment),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return $"{payloadSegment}.{Base64Url(signature)}";
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record Payload(
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
}
