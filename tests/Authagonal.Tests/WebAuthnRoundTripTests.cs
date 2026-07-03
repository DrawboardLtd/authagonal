using System.Buffers.Binary;
using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Authagonal.Core.Models;
using Authagonal.Server;
using Authagonal.Server.Services;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.AspNetCore.Http;

namespace Authagonal.Tests;

/// <summary>
/// End-to-end WebAuthn (passkey) verification through <see cref="WebAuthnService"/>, driven by a
/// software authenticator that produces cryptographically valid attestation/assertion responses.
/// This exercises the real fido2-net-lib verification path — signature, challenge binding, RP-ID
/// hash, origin, and signature-counter checks — rather than opaque recorded blobs, so tampering,
/// wrong-origin, wrong-challenge, and counter-rollback cases must all be rejected.
/// </summary>
public sealed class WebAuthnRoundTripTests
{
    private const string RpId = "test.authagonal.local";
    private const string Origin = "https://test.authagonal.local";

    // WebAuthnService now derives the FIDO2 relying party from the live request host (per-tenant hosts),
    // so drive it with an HTTP context whose host is RpId — giving ServerDomain=RpId and origin=Origin,
    // exactly what the VirtualAuthenticator signs over.
    private static WebAuthnService NewService()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Scheme = "https";
        ctx.Request.Host = new HostString(RpId);
        return new WebAuthnService(new StubHttpContextAccessor { HttpContext = ctx });
    }

    private sealed class StubHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    private static AuthUser TestUser() => new()
    {
        Id = "user-webauthn-1",
        Email = "passkey@acme.test",
        NormalizedEmail = "PASSKEY@ACME.TEST",
        FirstName = "Pass",
        LastName = "Key",
    };

    private static byte[] StoredPublicKey(MfaCredential cred)
    {
        var data = JsonSerializer.Deserialize(cred.PublicKeyJson!, AuthagonalJsonContext.Default.WebAuthnCredentialData)!;
        return Convert.FromBase64String(data.PublicKey);
    }

    [Fact]
    public async Task Attestation_ThenAssertion_RoundTrips()
    {
        var svc = NewService();
        var auth = new VirtualAuthenticator();
        var user = TestUser();

        var (opts, _) = svc.CreateAttestationOptions(user, []);
        var cred = await svc.CompleteAttestationAsync(user.Id, opts, auth.Attestation(opts.Challenge));

        Assert.Equal(MfaCredentialType.WebAuthn, cred.Type);
        Assert.NotNull(cred.PublicKeyJson);
        Assert.Equal(0u, cred.SignCount);
        var stored = JsonSerializer.Deserialize(cred.PublicKeyJson!, AuthagonalJsonContext.Default.WebAuthnCredentialData)!;
        Assert.Equal(auth.CredentialIdB64, stored.CredentialId);

        var assertOpts = svc.CreateAssertionOptions([cred]);
        var (success, credentialId, newSignCount) = await svc.CompleteAssertionAsync(
            assertOpts, auth.Assertion(assertOpts.Challenge, signCount: 1), StoredPublicKey(cred), storedSignCount: 0);

        Assert.True(success);
        Assert.Equal(1u, newSignCount);
        Assert.Equal(auth.CredentialId, credentialId);
    }

    [Fact]
    public async Task Assertion_WithTamperedSignature_IsRejected()
    {
        var svc = NewService();
        var auth = new VirtualAuthenticator();
        var (opts, _) = svc.CreateAttestationOptions(TestUser(), []);
        var cred = await svc.CompleteAttestationAsync("user-webauthn-1", opts, auth.Attestation(opts.Challenge));
        var assertOpts = svc.CreateAssertionOptions([cred]);

        await Assert.ThrowsAnyAsync<Exception>(() => svc.CompleteAssertionAsync(
            assertOpts, auth.Assertion(assertOpts.Challenge, signCount: 1, tamperSignature: true),
            StoredPublicKey(cred), storedSignCount: 0));
    }

    [Fact]
    public async Task Attestation_WithWrongChallenge_IsRejected()
    {
        var svc = NewService();
        var auth = new VirtualAuthenticator();
        var (opts, _) = svc.CreateAttestationOptions(TestUser(), []);

        // Sign over a challenge the relying party never issued.
        var bogus = RandomNumberGenerator.GetBytes(32);
        await Assert.ThrowsAnyAsync<Exception>(() => svc.CompleteAttestationAsync(
            "user-webauthn-1", opts, auth.Attestation(opts.Challenge, overrideChallenge: bogus)));
    }

    [Fact]
    public async Task Attestation_WithWrongOrigin_IsRejected()
    {
        var svc = NewService();
        var auth = new VirtualAuthenticator();
        var (opts, _) = svc.CreateAttestationOptions(TestUser(), []);

        await Assert.ThrowsAnyAsync<Exception>(() => svc.CompleteAttestationAsync(
            "user-webauthn-1", opts, auth.Attestation(opts.Challenge, overrideOrigin: "https://evil.example.com")));
    }

    [Fact]
    public async Task Assertion_WithRolledBackSignCounter_IsRejected()
    {
        var svc = NewService();
        var auth = new VirtualAuthenticator();
        var (opts, _) = svc.CreateAttestationOptions(TestUser(), []);
        var cred = await svc.CompleteAttestationAsync("user-webauthn-1", opts, auth.Attestation(opts.Challenge));
        var assertOpts = svc.CreateAssertionOptions([cred]);

        // Authenticator reports a counter lower than what we've already seen → cloned-key signal.
        await Assert.ThrowsAnyAsync<Exception>(() => svc.CompleteAssertionAsync(
            assertOpts, auth.Assertion(assertOpts.Challenge, signCount: 3), StoredPublicKey(cred), storedSignCount: 10));
    }

    /// <summary>
    /// Minimal CTAP-style authenticator: holds an ES256 key, builds spec-compliant authenticator
    /// data + a "none"-format attestation object, and signs assertions the way a real key would.
    /// </summary>
    private sealed class VirtualAuthenticator
    {
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public byte[] CredentialId { get; } = RandomNumberGenerator.GetBytes(20);
        public string CredentialIdB64 => Convert.ToBase64String(CredentialId);

        public AuthenticatorAttestationRawResponse Attestation(
            byte[] challenge, string? overrideOrigin = null, byte[]? overrideChallenge = null)
        {
            var clientData = ClientDataJson("webauthn.create", overrideChallenge ?? challenge, overrideOrigin ?? Origin);
            var authData = AuthData(includeAttestedCred: true, flags: 0x45 /* UP|UV|AT */, signCount: 0);

            var w = new CborWriter(CborConformanceMode.Lax);
            w.WriteStartMap(3);
            w.WriteTextString("fmt"); w.WriteTextString("none");
            w.WriteTextString("attStmt"); w.WriteStartMap(0); w.WriteEndMap();
            w.WriteTextString("authData"); w.WriteByteString(authData);
            w.WriteEndMap();

            return new AuthenticatorAttestationRawResponse
            {
                Id = B64Url(CredentialId),
                RawId = CredentialId,
                Type = PublicKeyCredentialType.PublicKey,
                Response = new AuthenticatorAttestationRawResponse.AttestationResponse
                {
                    AttestationObject = w.Encode(),
                    ClientDataJson = clientData,
                },
            };
        }

        public AuthenticatorAssertionRawResponse Assertion(byte[] challenge, uint signCount, bool tamperSignature = false)
        {
            var clientData = ClientDataJson("webauthn.get", challenge, Origin);
            var authData = AuthData(includeAttestedCred: false, flags: 0x05 /* UP|UV */, signCount: signCount);

            var toSign = new byte[authData.Length + 32];
            authData.CopyTo(toSign, 0);
            SHA256.HashData(clientData).CopyTo(toSign, authData.Length);
            var signature = _key.SignData(toSign, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            if (tamperSignature) signature[^1] ^= 0xFF;

            return new AuthenticatorAssertionRawResponse
            {
                Id = B64Url(CredentialId),
                RawId = CredentialId,
                Type = PublicKeyCredentialType.PublicKey,
                Response = new AuthenticatorAssertionRawResponse.AssertionResponse
                {
                    AuthenticatorData = authData,
                    ClientDataJson = clientData,
                    Signature = signature,
                    UserHandle = Encoding.UTF8.GetBytes("user-webauthn-1"),
                },
            };
        }

        private byte[] AuthData(bool includeAttestedCred, byte flags, uint signCount)
        {
            using var ms = new MemoryStream();
            ms.Write(SHA256.HashData(Encoding.UTF8.GetBytes(RpId)));
            ms.WriteByte(flags);
            Span<byte> count = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(count, signCount);
            ms.Write(count);
            if (includeAttestedCred)
            {
                ms.Write(new byte[16]); // AAGUID — zeros for "none" attestation
                Span<byte> idLen = stackalloc byte[2];
                BinaryPrimitives.WriteUInt16BigEndian(idLen, (ushort)CredentialId.Length);
                ms.Write(idLen);
                ms.Write(CredentialId);
                ms.Write(CosePublicKey());
            }
            return ms.ToArray();
        }

        private byte[] CosePublicKey()
        {
            var p = _key.ExportParameters(false);
            var w = new CborWriter(CborConformanceMode.Lax);
            w.WriteStartMap(5);
            w.WriteInt32(1); w.WriteInt32(2);    // kty: EC2
            w.WriteInt32(3); w.WriteInt32(-7);   // alg: ES256
            w.WriteInt32(-1); w.WriteInt32(1);   // crv: P-256
            w.WriteInt32(-2); w.WriteByteString(p.Q.X!);
            w.WriteInt32(-3); w.WriteByteString(p.Q.Y!);
            w.WriteEndMap();
            return w.Encode();
        }

        private static byte[] ClientDataJson(string type, byte[] challenge, string origin) =>
            Encoding.UTF8.GetBytes(
                $$"""{"type":"{{type}}","challenge":"{{B64Url(challenge)}}","origin":"{{origin}}","crossOrigin":false}""");

        private static string B64Url(byte[] b) =>
            Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
