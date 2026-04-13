using System.Security.Cryptography;
using System.Text.Json;
using Authagonal.Core.Models;
using Authagonal.Core.Stores;
using Microsoft.IdentityModel.Tokens;

namespace Authagonal.Server.Services;

/// <summary>
/// Shared signing key operations — key generation, rotation checks, JWKS building, RSA serialization.
/// Used by both single-tenant KeyManager and multi-tenant cloud wrappers.
/// </summary>
public static class SigningKeyOps
{
    public const int RsaKeySizeInBits = 2048;
    public const string Algorithm = SecurityAlgorithms.RsaSha256;

    /// <summary>
    /// Ensures an active signing key exists, generating one if missing or expired.
    /// Returns the active key.
    /// </summary>
    public static async Task<SigningKeyInfo> EnsureActiveKeyAsync(
        ISigningKeyStore keyStore, int keyLifetimeDays, ILogger logger, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var activeKey = await keyStore.GetActiveKeyAsync(ct);

        if (activeKey is null || activeKey.ExpiresAt <= now)
        {
            logger.LogInformation("Active signing key is missing or expired. Generating new RSA key pair");

            if (activeKey is not null)
                await keyStore.DeactivateKeyAsync(activeKey.KeyId, ct);

            activeKey = GenerateNewKey(now, keyLifetimeDays);
            await keyStore.StoreAsync(activeKey, ct);
        }

        return activeKey;
    }

    /// <summary>
    /// Checks if the active key is approaching expiry and rotates if needed.
    /// Returns true if rotation occurred.
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

    /// <summary>
    /// Builds signing credentials from a key.
    /// </summary>
    public static SigningCredentials BuildSigningCredentials(SigningKeyInfo key)
    {
        var rsaParams = DeserializeRsaParameters(key.RsaParametersJson);
        var rsa = RSA.Create();
        rsa.ImportParameters(rsaParams);
        var securityKey = new RsaSecurityKey(rsa) { KeyId = key.KeyId };
        return new SigningCredentials(securityKey, Algorithm);
    }

    /// <summary>
    /// Builds the JWKS list from all non-expired keys in the store.
    /// </summary>
    public static async Task<List<JsonWebKey>> BuildJwksAsync(
        ISigningKeyStore keyStore, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var allKeys = await keyStore.GetAllAsync(ct);
        var validKeys = new List<JsonWebKey>();

        foreach (var keyInfo in allKeys)
        {
            if (keyInfo.ExpiresAt <= now) continue;

            var rsaParams = DeserializeRsaParameters(keyInfo.RsaParametersJson);
            using var rsa = RSA.Create();
            rsa.ImportParameters(rsaParams);

            var securityKey = new RsaSecurityKey(rsa) { KeyId = keyInfo.KeyId };
            var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(securityKey);
            jwk.Use = JsonWebKeyUseNames.Sig;
            jwk.Alg = Algorithm;
            validKeys.Add(jwk);
        }

        return validKeys;
    }

    public static SigningKeyInfo GenerateNewKey(DateTimeOffset now, int lifetimeDays)
    {
        using var rsa = RSA.Create(RsaKeySizeInBits);
        var rsaParams = rsa.ExportParameters(includePrivateParameters: true);

        return new SigningKeyInfo
        {
            KeyId = Guid.NewGuid().ToString("N"),
            Algorithm = Algorithm,
            RsaParametersJson = SerializeRsaParameters(rsaParams),
            IsActive = true,
            CreatedAt = now,
            ExpiresAt = now.AddDays(lifetimeDays)
        };
    }

    public static string SerializeRsaParameters(RSAParameters p)
    {
        var dict = new Dictionary<string, string?>();
        if (p.Modulus is not null) dict["Modulus"] = Convert.ToBase64String(p.Modulus);
        if (p.Exponent is not null) dict["Exponent"] = Convert.ToBase64String(p.Exponent);
        if (p.D is not null) dict["D"] = Convert.ToBase64String(p.D);
        if (p.P is not null) dict["P"] = Convert.ToBase64String(p.P);
        if (p.Q is not null) dict["Q"] = Convert.ToBase64String(p.Q);
        if (p.DP is not null) dict["DP"] = Convert.ToBase64String(p.DP);
        if (p.DQ is not null) dict["DQ"] = Convert.ToBase64String(p.DQ);
        if (p.InverseQ is not null) dict["InverseQ"] = Convert.ToBase64String(p.InverseQ);
        return JsonSerializer.Serialize(dict!, AuthagonalJsonContext.Default.DictionaryStringString);
    }

    public static RSAParameters DeserializeRsaParameters(string json)
    {
        var dict = JsonSerializer.Deserialize(json, AuthagonalJsonContext.Default.DictionaryStringString)
            ?? throw new InvalidOperationException("Failed to deserialize RSA parameters");

        return new RSAParameters
        {
            Modulus = Convert.FromBase64String(dict["Modulus"]),
            Exponent = Convert.FromBase64String(dict["Exponent"]),
            D = dict.TryGetValue("D", out var d) ? Convert.FromBase64String(d) : null,
            P = dict.TryGetValue("P", out var p) ? Convert.FromBase64String(p) : null,
            Q = dict.TryGetValue("Q", out var q) ? Convert.FromBase64String(q) : null,
            DP = dict.TryGetValue("DP", out var dp) ? Convert.FromBase64String(dp) : null,
            DQ = dict.TryGetValue("DQ", out var dq) ? Convert.FromBase64String(dq) : null,
            InverseQ = dict.TryGetValue("InverseQ", out var iq) ? Convert.FromBase64String(iq) : null
        };
    }
}
