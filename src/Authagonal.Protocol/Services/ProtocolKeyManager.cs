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

    public SigningCredentials GetSigningCredentials()
    {
        return _signingCredentials
            ?? throw new InvalidOperationException("Signing key has not been initialized. Ensure ProtocolKeyManager is started.");
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
            var activeKey = await ProtocolSigningKeyOps.EnsureActiveKeyAsync(keyStore, lifetimeDays, _logger, ct);
            _signingCredentials = ProtocolSigningKeyOps.BuildSigningCredentials(activeKey);
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
