using System.Security.Cryptography;
using System.Text.Json;
using Authagonal.Core.Clustering;
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

    /// <summary>Resource name the generation lease contends on.</summary>
    private const string KeyGenerationLease = "authagonal-signing-key-generation";

    /// <summary>
    /// Long enough to cover a key generation plus the store round-trips, short enough that a node
    /// that dies mid-generation does not block the cluster for long.
    /// </summary>
    private static readonly TimeSpan KeyGenerationLeaseTtl = TimeSpan.FromSeconds(30);

    /// <param name="lease">
    /// Cluster lease provider, when the host has one. Generation is single-writer under it.
    /// </param>
    /// <param name="nodeId">This node's identity, for the lease.</param>
    /// <remarks>
    /// Generation used to be unguarded, and every node runs this at startup and again on each cache
    /// refresh. The rotation service that IS leader-gated only ever DEACTIVATES — it never generates —
    /// so its doc comment's "only the cluster leader performs the rotation to avoid concurrent key
    /// generation" described a guarantee nothing implemented. Worse, KeyRotationEnabled defaults to
    /// false, so in the default configuration rollover at expiry is driven entirely by this path, on
    /// every replica at once, with StoreAsync an unconditional upsert on every provider and
    /// GetActiveKeyAsync returning the FIRST active row — several simultaneously-active keys are
    /// representable, and which one is "the" active key can flap between reads.
    ///
    /// The lease makes generation single-writer wherever the host has one. Where it does not — a
    /// single-node deployment, or a host that registered no provider — behaviour is unchanged, which
    /// is correct: one node cannot race itself.
    /// </remarks>
    public static async Task<SigningKeyInfo> EnsureActiveKeyAsync(
        ISigningKeyStore keyStore, int keyLifetimeDays, ILogger logger, CancellationToken ct = default,
        ILeaseProvider? lease = null, string? nodeId = null)
    {
        var now = DateTimeOffset.UtcNow;
        var activeKey = await keyStore.GetActiveKeyAsync(ct);

        if (!NeedsGeneration(activeKey, now))
            return activeKey!;

        var holdsLease = false;
        if (lease is not null && !string.IsNullOrEmpty(nodeId))
        {
            try
            {
                holdsLease = await lease.TryAcquireOrRenewAsync(KeyGenerationLease, nodeId, KeyGenerationLeaseTtl, ct);
            }
            catch (Exception ex)
            {
                // A lease backend that is down must not stop the server from having a signing key —
                // an IdP that cannot sign is completely unavailable, which is worse than a duplicate
                // key. Proceed unguarded and say so.
                logger.LogWarning(ex, "Signing-key generation lease unavailable; generating without it");
            }

            if (!holdsLease)
            {
                // Another node is generating. Re-read rather than racing it; only generate if it did
                // not produce one, so a lease holder that dies mid-generation still resolves.
                await Task.Delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
                activeKey = await keyStore.GetActiveKeyAsync(ct);
                if (!NeedsGeneration(activeKey, DateTimeOffset.UtcNow))
                    return activeKey!;
            }
        }

        try
        {
            // Re-read under the lease: another node may have generated between the first read and the
            // acquisition, and generating again would deactivate the key it just published.
            if (holdsLease)
            {
                activeKey = await keyStore.GetActiveKeyAsync(ct);
                if (!NeedsGeneration(activeKey, DateTimeOffset.UtcNow))
                    return activeKey!;
            }

            logger.LogInformation("Active signing key missing/expired/unsupported algorithm. Generating new ES256 key");

            if (activeKey is not null)
                await keyStore.DeactivateKeyAsync(activeKey.KeyId, ct);

            activeKey = GenerateNewKey(DateTimeOffset.UtcNow, keyLifetimeDays);
            await keyStore.StoreAsync(activeKey, ct);
            return activeKey;
        }
        finally
        {
            if (holdsLease && lease is not null && nodeId is not null)
            {
                try { await lease.ReleaseAsync(KeyGenerationLease, nodeId, CancellationToken.None); }
                catch { /* best effort — the lease expires on its own */ }
            }
        }
    }

    private static bool NeedsGeneration(SigningKeyInfo? key, DateTimeOffset now) =>
        key is null || key.ExpiresAt <= now || !IsSupportedAlgorithm(key.Algorithm);

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

    /// <summary>
    /// How long a key stays published after its own expiry.
    /// </summary>
    /// <remarks>
    /// A key was dropped from JWKS at exactly <c>ExpiresAt</c>, with no grace for the tokens already
    /// minted under it. Access tokens live 1800s by default, so every token signed in the half hour
    /// before expiry was still inside its own <c>exp</c> while its <c>kid</c> had already vanished —
    /// and every relying party rejected it. A verification key is needed for as long as any token it
    /// signed can still be live, which is a different lifetime from "may still be used for signing".
    /// </remarks>
    public static readonly TimeSpan JwksRetentionGrace = TimeSpan.FromHours(2);

    public static async Task<List<JsonWebKey>> BuildJwksAsync(
        ISigningKeyStore keyStore, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var allKeys = await keyStore.GetAllAsync(ct);
        var validKeys = new List<JsonWebKey>();

        foreach (var keyInfo in allKeys)
        {
            // Retained past expiry — see JwksRetentionGrace. Publishing a key is not authorising its
            // use: signing is gated separately, on ExpiresAt itself.
            if (keyInfo.ExpiresAt.Add(JwksRetentionGrace) <= now) continue;
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
