using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Authagonal.Server.Services;

public sealed class KeyManager : IKeyManager, IHostedService, IDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<KeyManager> _logger;
    private readonly AuthOptions _authOptions;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private Timer? _refreshTimer;

    private SigningCredentials? _signingCredentials;
    private List<JsonWebKey> _allJsonWebKeys = [];

    public KeyManager(IServiceScopeFactory scopeFactory, ILogger<KeyManager> logger, IOptions<AuthOptions> authOptions)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _authOptions = authOptions.Value;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await RefreshKeysAsync(cancellationToken);

        var cacheRefreshInterval = TimeSpan.FromMinutes(_authOptions.SigningKeyCacheRefreshMinutes);
        _refreshTimer = new Timer(
            _ => _ = RefreshKeysInBackgroundAsync(),
            null,
            cacheRefreshInterval,
            cacheRefreshInterval);
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
            ?? throw new InvalidOperationException("Signing key has not been initialized. Ensure KeyManager is started.");
    }

    /// <summary>
    /// Forces an immediate key refresh from storage. Called by the rotation service
    /// after deactivating the old key so the new key is picked up promptly.
    /// </summary>
    public Task ForceRefreshAsync(CancellationToken ct = default) => RefreshKeysAsync(ct);

    public IReadOnlyList<JsonWebKey> GetSecurityKeys() => _allJsonWebKeys;

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

            var activeKey = await SigningKeyOps.EnsureActiveKeyAsync(
                keyStore, _authOptions.SigningKeyLifetimeDays, _logger, ct);

            _signingCredentials = SigningKeyOps.BuildSigningCredentials(activeKey);
            _allJsonWebKeys = await SigningKeyOps.BuildJwksAsync(keyStore, ct);

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
