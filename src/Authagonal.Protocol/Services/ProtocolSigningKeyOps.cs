using System.Security.Cryptography;
using System.Text.Json;
using Authagonal.Core.Models;
using Authagonal.Core.Stores;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace Authagonal.Protocol.Services;

/// <summary>
/// Signing-key operations — generation, rotation checks, JWKS assembly, EC key serialization.
/// Public so host-side rotation services (e.g. cluster-aware rotation) can reuse them.
///
/// Authagonal signs JWTs with ES256 (ECDSA P-256 + SHA-256). Per-token signing cost is roughly
/// an order of magnitude lower than RSA-2048; tokens and JWKS are smaller. Historical RSA keys
/// in storage are ignored at refresh time and replaced.
/// </summary>
public static class ProtocolSigningKeyOps
{
    public const string Algorithm = SecurityAlgorithms.EcdsaSha256;
    private const string CurveName = "P-256";

    public static async Task<SigningKeyInfo> EnsureActiveKeyAsync(
        ISigningKeyStore keyStore, int keyLifetimeDays, ILogger logger, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var activeKey = await keyStore.GetActiveKeyAsync(ct);

        if (activeKey is null || activeKey.ExpiresAt <= now || !IsSupportedAlgorithm(activeKey.Algorithm))
        {
            logger.LogInformation("Active signing key missing/expired/unsupported algorithm. Generating new ES256 key");

            if (activeKey is not null)
                await keyStore.DeactivateKeyAsync(activeKey.KeyId, ct);

            activeKey = GenerateNewKey(now, keyLifetimeDays);
            await keyStore.StoreAsync(activeKey, ct);
        }

        return activeKey;
    }

    /// <summary>
    /// Checks whether the active signing key is approaching expiry and rotates if so.
    /// Returns true if rotation occurred. Callers should <c>ForceRefreshAsync</c> the key
    /// manager after a successful rotation so the new key is picked up promptly.
    /// </summary>
    public static async Task<bool> CheckAndRotateAsync(
        ISigningKeyStore keyStore, int keyLifetimeDays, int leadTimeDays,
        ILogger logger, CancellationToken ct = default)
    {
        var activeKey = await keyStore.GetActiveKeyAsync(ct);
        if (activeKey is null)
        {
            logger.LogWarning("No active signing key found — will be generated on next refresh");
            return false;
        }

        var timeUntilExpiry = activeKey.ExpiresAt - DateTimeOffset.UtcNow;
        var rotationThreshold = TimeSpan.FromDays(leadTimeDays);

        if (timeUntilExpiry > rotationThreshold)
        {
            logger.LogDebug(
                "Active key {KeyId} expires in {Days:F0} days — no rotation needed (threshold: {Threshold} days)",
                activeKey.KeyId, timeUntilExpiry.TotalDays, leadTimeDays);
            return false;
        }

        logger.LogInformation(
            "Active key {KeyId} expires in {Days:F0} days (threshold: {Threshold} days). Rotating",
            activeKey.KeyId, timeUntilExpiry.TotalDays, leadTimeDays);

        await keyStore.DeactivateKeyAsync(activeKey.KeyId, ct);
        return true;
    }

    public static SigningCredentials BuildSigningCredentials(SigningKeyInfo key)
    {
        var ecParams = DeserializeEcParameters(key.KeyMaterialJson);
        var ecdsa = ECDsa.Create(ecParams);
        var securityKey = new ECDsaSecurityKey(ecdsa) { KeyId = key.KeyId };
        return new SigningCredentials(securityKey, Algorithm);
    }

    public static async Task<List<JsonWebKey>> BuildJwksAsync(
        ISigningKeyStore keyStore, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var allKeys = await keyStore.GetAllAsync(ct);
        var validKeys = new List<JsonWebKey>();

        foreach (var keyInfo in allKeys)
        {
            if (keyInfo.ExpiresAt <= now) continue;
            if (!IsSupportedAlgorithm(keyInfo.Algorithm)) continue;

            // Hand-build the JWK rather than using JsonWebKeyConverter — older versions of
            // Microsoft.IdentityModel don't reliably populate Crv from an in-memory ECDsa
            // when the curve was attached via ECParameters (vs. from a key file).
            var ecParams = DeserializeEcParameters(keyInfo.KeyMaterialJson);
            var jwk = new JsonWebKey
            {
                Kty = "EC",
                Crv = CurveName,
                X = ecParams.Q.X is null ? null : Base64UrlEncoder.Encode(ecParams.Q.X),
                Y = ecParams.Q.Y is null ? null : Base64UrlEncoder.Encode(ecParams.Q.Y),
                Kid = keyInfo.KeyId,
                Use = JsonWebKeyUseNames.Sig,
                Alg = Algorithm,
            };
            validKeys.Add(jwk);
        }

        return validKeys;
    }

    public static SigningKeyInfo GenerateNewKey(DateTimeOffset now, int lifetimeDays)
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var ecParams = ecdsa.ExportParameters(includePrivateParameters: true);

        return new SigningKeyInfo
        {
            KeyId = Guid.NewGuid().ToString("N"),
            Algorithm = Algorithm,
            KeyMaterialJson = SerializeEcParameters(ecParams),
            IsActive = true,
            CreatedAt = now,
            ExpiresAt = now.AddDays(lifetimeDays)
        };
    }

    private static bool IsSupportedAlgorithm(string algorithm) =>
        string.Equals(algorithm, Algorithm, StringComparison.Ordinal);

    /// <summary>
    /// Converts a <see cref="JsonWebKey"/> back to a typed <see cref="SecurityKey"/>
    /// so JwtBearer's signature-validation pipeline can resolve a CryptoProviderFactory
    /// reliably. Used wherever we need to validate tokens against the keys advertised
    /// in the JWKS.
    /// </summary>
    public static SecurityKey JwkToSecurityKey(JsonWebKey jwk) => jwk.Kty switch
    {
        "EC" => new ECDsaSecurityKey(ECDsa.Create(new ECParameters
        {
            Curve = jwk.Crv switch
            {
                "P-256" => ECCurve.NamedCurves.nistP256,
                "P-384" => ECCurve.NamedCurves.nistP384,
                "P-521" => ECCurve.NamedCurves.nistP521,
                _ => throw new InvalidOperationException($"Unsupported EC curve: {jwk.Crv}"),
            },
            Q = new ECPoint
            {
                X = Base64UrlEncoder.DecodeBytes(jwk.X),
                Y = Base64UrlEncoder.DecodeBytes(jwk.Y),
            }
        }))
        { KeyId = jwk.Kid },
        "RSA" => new RsaSecurityKey(new RSAParameters
        {
            Modulus = Base64UrlEncoder.DecodeBytes(jwk.N),
            Exponent = Base64UrlEncoder.DecodeBytes(jwk.E),
        })
        { KeyId = jwk.Kid },
        _ => jwk,
    };

    private static string SerializeEcParameters(ECParameters p)
    {
        var dict = new Dictionary<string, string> { ["Curve"] = CurveName };
        if (p.D is not null) dict["D"] = Convert.ToBase64String(p.D);
        if (p.Q.X is not null) dict["QX"] = Convert.ToBase64String(p.Q.X);
        if (p.Q.Y is not null) dict["QY"] = Convert.ToBase64String(p.Q.Y);
        return JsonSerializer.Serialize(dict, ProtocolJsonContext.Default.DictionaryStringString);
    }

    private static ECParameters DeserializeEcParameters(string json)
    {
        var dict = JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.DictionaryStringString)
            ?? throw new InvalidOperationException("Failed to deserialize EC parameters");

        if (!dict.TryGetValue("QX", out var qx) || !dict.TryGetValue("QY", out var qy))
            throw new InvalidOperationException("EC parameter blob missing QX/QY (legacy RSA key?)");

        return new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = dict.TryGetValue("D", out var d) ? Convert.FromBase64String(d) : null,
            Q = new ECPoint
            {
                X = Convert.FromBase64String(qx),
                Y = Convert.FromBase64String(qy),
            }
        };
    }
}
