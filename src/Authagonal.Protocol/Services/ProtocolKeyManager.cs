using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Authagonal.Protocol.Services;

/// <summary>
/// Protocol's implementation of <see cref="IKeyManager"/>. Ensures an active signing key exists
/// at startup and caches signing credentials in memory. Refreshes periodically to pick up
/// externally rotated keys. No cluster awareness — suited to embedded single-tenant hosts.
/// </summary>
public sealed class ProtocolKeyManager : IKeyManager, IHostedService, IDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ProtocolKeyManager> _logger;
    private readonly IOptionsMonitor<AuthagonalProtocolOptions> _options;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private Timer? _refreshTimer;

    private SigningCredentials? _signingCredentials;

    /// <summary>Expiry of the key behind <see cref="_signingCredentials"/>, so a stale cache cannot
    /// keep signing with a retired key between refreshes.</summary>
    private DateTimeOffset? _signingKeyExpiresAt;
    private List<JsonWebKey> _allJsonWebKeys = [];

    public ProtocolKeyManager(
        IServiceScopeFactory scopeFactory,
        ILogger<ProtocolKeyManager> logger,
        IOptionsMonitor<AuthagonalProtocolOptions> options)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = options;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await RefreshKeysAsync(cancellationToken);
        var cacheRefresh = TimeSpan.FromMinutes(_options.CurrentValue.SigningKeyCacheRefreshMinutes);
        _refreshTimer = new Timer(
            _ => _ = RefreshKeysInBackgroundAsync(),
            null,
            cacheRefresh,
            cacheRefresh);
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
    }

    /// <remarks>
    /// The expiry check is the point. This returned whatever was cached, and the cache refreshes only
    /// every <c>SigningKeyCacheRefreshMinutes</c> (60 by default) — so for up to an hour after a key
    /// expired, a node that had not yet refreshed kept MINTING tokens with a retired key, whose JWK
    /// every refreshed node had already stopped publishing. Those tokens were unverifiable by anyone
    /// from the moment they were issued. Worse, a key-store outage makes the background refresh
    /// swallow its exception, so the stale credentials would have been used indefinitely.
    /// <para>
    /// Failing loudly is the right direction: refusing to mint is a visible outage, while minting
    /// with a retired key produces tokens that fail at every relying party for reasons none of them
    /// can diagnose.
    /// </para>
    /// </remarks>
    public SigningCredentials GetSigningCredentials()
    {
        var credentials = _signingCredentials
            ?? throw new InvalidOperationException("Signing key has not been initialized. Ensure ProtocolKeyManager is started.");

        if (_signingKeyExpiresAt is { } expiresAt && expiresAt <= DateTimeOffset.UtcNow)
        {
            throw new InvalidOperationException(
                $"The cached signing key '{credentials.Key.KeyId}' expired at {expiresAt:O}. Refusing to " +
                "sign with a retired key — tokens minted under it are no longer verifiable. A refresh " +
                "is due; if this persists the signing-key store is unreachable.");
        }

        return credentials;
    }

    public IReadOnlyList<JsonWebKey> GetSecurityKeys() => _allJsonWebKeys;

    public Task ForceRefreshAsync(CancellationToken ct = default) => RefreshKeysAsync(ct);

    private async Task RefreshKeysInBackgroundAsync()
    {
        try { await RefreshKeysAsync(CancellationToken.None); }
        catch (Exception ex) { _logger.LogError(ex, "Failed to refresh signing keys in background"); }
    }

    private async Task RefreshKeysAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var keyStore = scope.ServiceProvider.GetRequiredService<ISigningKeyStore>();

            var lifetimeDays = _options.CurrentValue.SigningKeyLifetimeDays;
            // The lease and node identity are optional — a host without clustering registers neither,
            // and a single node cannot race itself.
            var lease = scope.ServiceProvider.GetService<Authagonal.Core.Clustering.ILeaseProvider>();
            var nodeId = scope.ServiceProvider.GetService<Authagonal.Core.Clustering.ILeaderElection>()?.NodeId;

            var activeKey = await ProtocolSigningKeyOps.EnsureActiveKeyAsync(
                keyStore, lifetimeDays, _logger, ct, lease, nodeId);
            _signingCredentials = ProtocolSigningKeyOps.BuildSigningCredentials(activeKey);
            _signingKeyExpiresAt = activeKey.ExpiresAt;
            _allJsonWebKeys = await ProtocolSigningKeyOps.BuildJwksAsync(keyStore, ct);

            _logger.LogInformation(
                "Signing keys refreshed. Active key: {KeyId}, Total valid keys in JWKS: {Count}",
                activeKey.KeyId, _allJsonWebKeys.Count);
        }
        finally
        {
            _lock.Release();
        }
    }
}
