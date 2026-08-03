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

            // Promote the successor that was published ahead, if there is one.
            //
            // This deactivated the old key and generated its replacement in the same breath, and
            // BuildSigningCredentials then made that brand-new key the signer immediately — so a kid first
            // appeared in JWKS at the exact instant it started signing. Peers cache keys for
            // SigningKeyCacheRefreshMinutes and both JWKS endpoints send `max-age=3600`, so every token minted
            // under the new key was rejected by peer nodes and by any shared cache until their TTL lapsed.
            // Both endpoints' comments assert the opposite — "the next key is published days ahead of use, so
            // a short shared cache is always safe" — and SigningKeyRotationTests
            // .Rotation_PublishAhead_NewKeyActive_OldKeyStillInJwks is named for a publish-ahead that did not
            // exist.
            //
            // PublishSuccessorIfDueAsync puts the successor in the store INACTIVE well before this point, and
            // BuildJwksAsync publishes every unexpired key regardless of IsActive — so by the time it is
            // promoted here, every peer and every cache has already seen it.
            var successor = await FindPublishedSuccessorAsync(keyStore, DateTimeOffset.UtcNow, keyLifetimeDays, ct);
            if (successor is not null)
            {
                logger.LogInformation(
                    "Promoting pre-published signing key {KeyId} (published {Age:F0}h ago) to active",
                    successor.KeyId, (DateTimeOffset.UtcNow - successor.CreatedAt).TotalHours);

                if (activeKey is not null)
                    await keyStore.DeactivateKeyAsync(activeKey.KeyId, ct);

                successor.IsActive = true;
                await keyStore.StoreAsync(successor, ct);
                return successor;
            }

            // Nothing was published ahead — first boot, or a key that expired faster than the publish-ahead
            // window. Mint one now and accept the cache lag, which is the old behaviour and the only option.
            logger.LogInformation(
                "Active signing key missing/expired/unsupported algorithm and no successor was published "
                + "ahead. Generating a new ES256 key, which peers will not know until their key cache "
                + "refreshes.");

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

    /// <summary>
    /// How long before an active key's expiry its successor is generated and published.
    /// </summary>
    /// <remarks>
    /// Has to exceed the longest window in which another party could still be holding a stale key set: the
    /// JWKS <c>max-age</c> both endpoints send (1 hour) and the in-process
    /// <c>SigningKeyCacheRefreshMinutes</c>. A day is generously past both and costs only one extra published
    /// key, which is what a JWKS is for.
    /// </remarks>
    private static readonly TimeSpan PublishAheadWindow = TimeSpan.FromDays(1);

    /// <summary>
    /// An unexpired, inactive key already in the store — the successor published ahead of promotion.
    /// </summary>
    /// <remarks>
    /// Newest first, so a store that somehow holds several inactive keys promotes the freshest rather than one
    /// about to expire. Keys past their own expiry are skipped: they are retained in JWKS for verification
    /// (see <c>JwksRetentionGrace</c>) but must never be promoted to signer.
    /// </remarks>
    private static async Task<SigningKeyInfo?> FindPublishedSuccessorAsync(
        ISigningKeyStore keyStore, DateTimeOffset now, int keyLifetimeDays, CancellationToken ct)
    {
        // More than half its life left is what separates a PUBLISHED SUCCESSOR from a RETIRED key.
        //
        // Both are inactive and unexpired, and the first version of this looked only at IsActive — so
        // immediately after CheckAndRotateAsync deactivated the outgoing key, this found that very key and
        // promoted it straight back. The suite caught it: Rotation_PublishAhead_NewKeyActive_OldKeyStillInJwks
        // failed with fresh.KeyId == old.KeyId.
        //
        // A key is retired because it came within the rotation lead time of expiry, so it has little life
        // left; a successor is minted with a full lifetime. Half is comfortably between the two and needs no
        // new column on SigningKeyInfo — and refusing to promote a key that is itself nearly due for rotation
        // is the right rule regardless of how it came to be inactive.
        var minimumRemaining = TimeSpan.FromDays(keyLifetimeDays) / 2;

        var all = await keyStore.GetAllAsync(ct);
        return all
            .Where(k => !k.IsActive
                        && k.ExpiresAt > now
                        && k.ExpiresAt - now > minimumRemaining
                        && IsSupportedAlgorithm(k.Algorithm))
            .OrderByDescending(k => k.CreatedAt)
            .FirstOrDefault();
    }

    /// <summary>
    /// Publishes the next signing key ahead of its use, so no verifier meets it for the first time in a token.
    /// </summary>
    /// <remarks>
    /// Stored INACTIVE: <see cref="BuildJwksAsync"/> advertises every unexpired key whatever its IsActive
    /// state, while signing is chosen by <c>GetActiveKeyAsync</c>. So the successor is published and unused
    /// until <see cref="EnsureActiveKeyAsync"/> promotes it, which is exactly what both JWKS endpoints already
    /// told callers happens.
    /// <para>
    /// Idempotent, and lease-guarded like generation is: a cluster must publish ONE successor, not one per
    /// node. Returns true when it published, so the caller can log it.
    /// </para>
    /// </remarks>
    public static async Task<bool> PublishSuccessorIfDueAsync(
        ISigningKeyStore keyStore,
        int keyLifetimeDays,
        ILogger logger,
        CancellationToken ct = default,
        Authagonal.Core.Clustering.ILeaseProvider? lease = null,
        string? nodeId = null)
    {
        var now = DateTimeOffset.UtcNow;
        var activeKey = await keyStore.GetActiveKeyAsync(ct);

        // No active key at all is EnsureActiveKeyAsync's job, not this one.
        if (activeKey is null || activeKey.ExpiresAt - now > PublishAheadWindow)
            return false;

        // Already published — nothing to do. Checked before taking the lease so the common case is one read.
        if (await FindPublishedSuccessorAsync(keyStore, now, keyLifetimeDays, ct) is not null)
            return false;

        var holdsLease = false;
        if (lease is not null && !string.IsNullOrEmpty(nodeId))
        {
            try
            {
                holdsLease = await lease.TryAcquireOrRenewAsync(KeyGenerationLease, nodeId, KeyGenerationLeaseTtl, ct);
            }
            catch (Exception ex)
            {
                // Unlike generation, publishing ahead is not urgent: if the lease backend is down, skip this
                // pass and try again on the next refresh rather than risk every node publishing its own.
                logger.LogWarning(ex, "Signing-key publish-ahead lease unavailable; skipping this pass");
                return false;
            }

            if (!holdsLease) return false;
        }

        try
        {
            // Re-read under the lease: another node may have published between the check above and here.
            if (await FindPublishedSuccessorAsync(keyStore, DateTimeOffset.UtcNow, keyLifetimeDays, ct) is not null)
                return false;

            var successor = GenerateNewKey(DateTimeOffset.UtcNow, keyLifetimeDays);
            successor.IsActive = false;
            await keyStore.StoreAsync(successor, ct);

            logger.LogInformation(
                "Published successor signing key {KeyId} ahead of use; active key {ActiveKeyId} expires in "
                + "{Hours:F0}h. It will be promoted to signer only after every verifier has had the chance to "
                + "see it.",
                successor.KeyId, activeKey.KeyId, (activeKey.ExpiresAt - now).TotalHours);

            return true;
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
