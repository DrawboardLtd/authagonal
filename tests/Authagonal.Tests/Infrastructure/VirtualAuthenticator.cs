using System.Buffers.Binary;
using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text;
using Fido2NetLib;
using Fido2NetLib.Objects;

namespace Authagonal.Tests.Infrastructure;

/// <summary>
/// Minimal CTAP-style authenticator: holds an ES256 key, builds spec-compliant authenticator data + a
/// "none"-format attestation object, and signs assertions the way a real key would. Verification runs
/// through the real fido2-net-lib path, so anything this produces is accepted or refused on its merits
/// rather than because a recorded blob was replayed.
/// </summary>
/// <remarks>
/// Lifted out of WebAuthnRoundTripTests so the HTTP-level ceremony tests can drive the same
/// authenticator. Those tests assert how the endpoints map a refusal onto a status code and an error
/// string, which is the part a browser client actually sees and the part a service-level test cannot
/// reach — so they need an authenticator that produces genuinely valid material, or a 401 proves
/// nothing about the check under test.
/// </remarks>
public sealed class VirtualAuthenticator(string rpId, string origin)
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public byte[] CredentialId { get; } = RandomNumberGenerator.GetBytes(20);
    public string CredentialIdB64 => Convert.ToBase64String(CredentialId);

    public AuthenticatorAttestationRawResponse Attestation(
        byte[] challenge, string? overrideOrigin = null, byte[]? overrideChallenge = null)
    {
        var clientData = ClientDataJson("webauthn.create", overrideChallenge ?? challenge, overrideOrigin ?? origin);
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

    /// <param name="userHandle">
    /// The user handle the authenticator reports. A discoverable credential always returns one; passing
    /// <see langword="null"/> models an authenticator (or a client) that omits it, which WebAuthn §7.2
    /// step 6 forbids for a ceremony where the user was not identified beforehand.
    /// </param>
    /// <param name="asRpId">
    /// Sign for a DIFFERENT relying party with the same key. Models what a man-in-the-middle host gets
    /// from an authenticator that will talk to it: material that is internally consistent and verifies
    /// against the enrolled public key, but belongs to another RP than the one storage recorded.
    /// </param>
    public AuthenticatorAssertionRawResponse Assertion(
        byte[] challenge, uint signCount, bool tamperSignature = false, byte[]? userHandle = null,
        string? asRpId = null, string? asOrigin = null)
    {
        var clientData = ClientDataJson("webauthn.get", challenge, asOrigin ?? origin);
        var authData = AuthData(includeAttestedCred: false, flags: 0x05 /* UP|UV */, signCount: signCount,
            rpIdOverride: asRpId);

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
                UserHandle = userHandle,
            },
        };
    }

    private byte[] AuthData(bool includeAttestedCred, byte flags, uint signCount, string? rpIdOverride = null)
    {
        using var ms = new MemoryStream();
        ms.Write(SHA256.HashData(Encoding.UTF8.GetBytes(rpIdOverride ?? rpId)));
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

    public static string B64Url(byte[] b) =>
        Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
