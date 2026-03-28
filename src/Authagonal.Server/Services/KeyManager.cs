using System.Security.Cryptography;
using System.Text.Json;
using Authagonal.Core.Models;
using Authagonal.Core.Stores;
using Microsoft.IdentityModel.Tokens;

namespace Authagonal.Server.Services;

public sealed class KeyManager : IHostedService, IDisposable
{
    private static readonly TimeSpan KeyLifetime = TimeSpan.FromDays(90);
    private static readonly TimeSpan CacheRefreshInterval = TimeSpan.FromMinutes(60);
    private const int RsaKeySizeInBits = 2048;
    private const string Algorithm = SecurityAlgorithms.RsaSha256;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<KeyManager> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private Timer? _refreshTimer;

    private SigningCredentials? _signingCredentials;
    private RsaSecurityKey? _activeSecurityKey;
    private List<JsonWebKey> _allJsonWebKeys = [];

    public KeyManager(IServiceScopeFactory scopeFactory, ILogger<KeyManager> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await RefreshKeysAsync(cancellationToken);

        _refreshTimer = new Timer(
            _ => _ = RefreshKeysInBackgroundAsync(),
            null,
            CacheRefreshInterval,
            CacheRefreshInterval);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _refreshTimer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _refreshTimer?.Dispose();
        _lock.Dispose();
        _activeSecurityKey?.Rsa?.Dispose();
    }

    /// <summary>
    /// Returns the current signing credentials for creating JWTs.
    /// </summary>
    public SigningCredentials GetSigningCredentials()
    {
        return _signingCredentials
            ?? throw new InvalidOperationException("Signing key has not been initialized. Ensure KeyManager is started.");
    }

    /// <summary>
    /// Returns all valid JSON Web Keys for the JWKS endpoint.
    /// Includes old keys that haven't expired yet (for token validation during rotation).
    /// </summary>
    public IReadOnlyList<JsonWebKey> GetSecurityKeys()
    {
        return _allJsonWebKeys;
    }

    private async Task RefreshKeysInBackgroundAsync()
    {
        try
        {
            await RefreshKeysAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh signing keys in background");
        }
    }

    private async Task RefreshKeysAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var keyStore = scope.ServiceProvider.GetRequiredService<ISigningKeyStore>();

            var activeKey = await keyStore.GetActiveKeyAsync(ct);
            var now = DateTimeOffset.UtcNow;

            if (activeKey is null || activeKey.ExpiresAt <= now)
            {
                _logger.LogInformation("Active signing key is missing or expired. Generating new RSA key pair");

                if (activeKey is not null)
                {
                    await keyStore.DeactivateKeyAsync(activeKey.KeyId, ct);
                }

                activeKey = GenerateNewKey(now);
                await keyStore.StoreAsync(activeKey, ct);
            }

            // Load the active key into memory
            var rsaParams = DeserializeRsaParameters(activeKey.RsaParametersJson);
            var rsa = RSA.Create();
            rsa.ImportParameters(rsaParams);

            var securityKey = new RsaSecurityKey(rsa) { KeyId = activeKey.KeyId };
            _activeSecurityKey = securityKey;
            _signingCredentials = new SigningCredentials(securityKey, Algorithm);

            // Build JWKS: include all non-expired keys (active + rotated-but-still-valid)
            var allKeys = await keyStore.GetAllAsync(ct);
            var validKeys = new List<JsonWebKey>();

            foreach (var keyInfo in allKeys)
            {
                if (keyInfo.ExpiresAt <= now)
                    continue;

                var keyRsaParams = DeserializeRsaParameters(keyInfo.RsaParametersJson);
                using var keyRsa = RSA.Create();
                keyRsa.ImportParameters(keyRsaParams);

                var keySec = new RsaSecurityKey(keyRsa) { KeyId = keyInfo.KeyId };
                var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(keySec);
                jwk.Use = JsonWebKeyUseNames.Sig;
                jwk.Alg = Algorithm;
                validKeys.Add(jwk);
            }

            _allJsonWebKeys = validKeys;

            _logger.LogInformation(
                "Signing keys refreshed. Active key: {KeyId}, Total valid keys in JWKS: {Count}",
                activeKey.KeyId, validKeys.Count);
        }
        finally
        {
            _lock.Release();
        }
    }

    private static SigningKeyInfo GenerateNewKey(DateTimeOffset now)
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
            ExpiresAt = now.Add(KeyLifetime)
        };
    }

    private static string SerializeRsaParameters(RSAParameters p)
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
        return JsonSerializer.Serialize(dict);
    }

    private static RSAParameters DeserializeRsaParameters(string json)
    {
        var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json)
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
