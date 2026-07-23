using Authagonal.Core.Models;

namespace Authagonal.Tests;

/// <summary>M10 + L10: the shared WebAuthn credential-id extraction. It absorbs malformed data (returns
/// false, never throws) so index cleanup can skip it — while, because it lives in a pure method with NO
/// try/catch at the store call site, a real storage fault on the index delete now propagates instead of
/// being swallowed.</summary>
public class MfaCredentialTests
{
    private static MfaCredential WebAuthn(string? publicKeyJson) => new()
    {
        Id = "c1",
        UserId = "u1",
        Type = MfaCredentialType.WebAuthn,
        PublicKeyJson = publicKeyJson,
    };

    [Fact]
    public void ValidJson_extractsCredentialId()
    {
        var raw = new byte[] { 1, 2, 3, 4, 250 };
        var cred = WebAuthn($"{{\"credentialId\":\"{Convert.ToBase64String(raw)}\",\"publicKey\":\"x\"}}");
        Assert.True(cred.TryGetWebAuthnCredentialId(out var id));
        Assert.Equal(raw, id);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{}")]                                     // no credentialId property
    [InlineData("{\"credentialId\":\"!!not base64!!\"}")]  // bad base64
    [InlineData("{\"credentialId\":\"\"}")]                // empty
    public void MalformedOrMissing_returnsFalse_neverThrows(string publicKeyJson)
    {
        Assert.False(WebAuthn(publicKeyJson).TryGetWebAuthnCredentialId(out var id));
        Assert.Empty(id);
    }

    [Fact]
    public void NonWebAuthnFactor_returnsFalse()
    {
        var totp = new MfaCredential { Id = "c", UserId = "u", Type = MfaCredentialType.Totp, SecretProtected = "s" };
        Assert.False(totp.TryGetWebAuthnCredentialId(out _));
    }
}
