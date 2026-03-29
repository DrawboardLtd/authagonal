using System.Collections.Concurrent;
using Authagonal.Core.Models;
using Authagonal.Core.Stores;

namespace Authagonal.Tests.Infrastructure;

public sealed class InMemoryUserStore : IUserStore
{
    private readonly ConcurrentDictionary<string, AuthUser> _users = new();
    private readonly ConcurrentDictionary<string, ExternalLoginInfo> _logins = new(); // key: provider|providerKey

    public Task<AuthUser?> GetAsync(string userId, CancellationToken ct = default)
        => Task.FromResult(_users.GetValueOrDefault(userId));

    public Task<AuthUser?> FindByEmailAsync(string email, CancellationToken ct = default)
    {
        var normalized = email.ToUpperInvariant();
        var user = _users.Values.FirstOrDefault(u => u.NormalizedEmail == normalized);
        return Task.FromResult(user);
    }

    public Task CreateAsync(AuthUser user, CancellationToken ct = default)
    {
        _users[user.Id] = user;
        return Task.CompletedTask;
    }

    public Task UpdateAsync(AuthUser user, CancellationToken ct = default)
    {
        _users[user.Id] = user;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string userId, CancellationToken ct = default)
    {
        _users.TryRemove(userId, out _);
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string userId, CancellationToken ct = default)
        => Task.FromResult(_users.ContainsKey(userId));

    public Task AddLoginAsync(ExternalLoginInfo login, CancellationToken ct = default)
    {
        _logins[$"{login.Provider}|{login.ProviderKey}"] = login;
        return Task.CompletedTask;
    }

    public Task RemoveLoginAsync(string userId, string provider, string providerKey, CancellationToken ct = default)
    {
        _logins.TryRemove($"{provider}|{providerKey}", out _);
        return Task.CompletedTask;
    }

    public Task<ExternalLoginInfo?> FindLoginAsync(string provider, string providerKey, CancellationToken ct = default)
        => Task.FromResult(_logins.GetValueOrDefault($"{provider}|{providerKey}"));

    public Task<IReadOnlyList<ExternalLoginInfo>> GetLoginsAsync(string userId, CancellationToken ct = default)
    {
        var logins = _logins.Values.Where(l => l.UserId == userId).ToList();
        return Task.FromResult<IReadOnlyList<ExternalLoginInfo>>(logins);
    }
}

public sealed class InMemoryClientStore : IClientStore
{
    private readonly ConcurrentDictionary<string, OAuthClient> _clients = new();

    public Task<OAuthClient?> GetAsync(string clientId, CancellationToken ct = default)
        => Task.FromResult(_clients.GetValueOrDefault(clientId));

    public Task<IReadOnlyList<OAuthClient>> GetAllAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<OAuthClient>>(_clients.Values.ToList());

    public Task UpsertAsync(OAuthClient client, CancellationToken ct = default)
    {
        _clients[client.ClientId] = client;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string clientId, CancellationToken ct = default)
    {
        _clients.TryRemove(clientId, out _);
        return Task.CompletedTask;
    }
}

public sealed class InMemoryGrantStore : IGrantStore
{
    private readonly ConcurrentDictionary<string, PersistedGrant> _grants = new();

    public Task StoreAsync(PersistedGrant grant, CancellationToken ct = default)
    {
        _grants[grant.Key] = grant;
        return Task.CompletedTask;
    }

    public Task<PersistedGrant?> GetAsync(string key, CancellationToken ct = default)
        => Task.FromResult(_grants.GetValueOrDefault(key));

    public Task ConsumeAsync(string key, CancellationToken ct = default)
    {
        if (_grants.TryGetValue(key, out var grant))
            grant.ConsumedAt = DateTimeOffset.UtcNow;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key, CancellationToken ct = default)
    {
        _grants.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public Task RemoveAllBySubjectAsync(string subjectId, CancellationToken ct = default)
    {
        foreach (var key in _grants.Where(kvp => kvp.Value.SubjectId == subjectId).Select(kvp => kvp.Key))
            _grants.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public Task RemoveAllBySubjectAndClientAsync(string subjectId, string clientId, CancellationToken ct = default)
    {
        foreach (var key in _grants.Where(kvp => kvp.Value.SubjectId == subjectId && kvp.Value.ClientId == clientId).Select(kvp => kvp.Key))
            _grants.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PersistedGrant>> GetBySubjectAsync(string subjectId, CancellationToken ct = default)
    {
        var grants = _grants.Values.Where(g => g.SubjectId == subjectId).ToList();
        return Task.FromResult<IReadOnlyList<PersistedGrant>>(grants);
    }

    public Task RemoveExpiredAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        foreach (var key in _grants.Where(kvp => kvp.Value.ExpiresAt <= cutoff).Select(kvp => kvp.Key))
            _grants.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}

public sealed class InMemorySigningKeyStore : ISigningKeyStore
{
    private readonly ConcurrentDictionary<string, SigningKeyInfo> _keys = new();

    public Task<SigningKeyInfo?> GetActiveKeyAsync(CancellationToken ct = default)
    {
        var active = _keys.Values.FirstOrDefault(k => k.IsActive && k.ExpiresAt > DateTimeOffset.UtcNow);
        return Task.FromResult(active);
    }

    public Task<IReadOnlyList<SigningKeyInfo>> GetAllAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<SigningKeyInfo>>(_keys.Values.ToList());

    public Task StoreAsync(SigningKeyInfo key, CancellationToken ct = default)
    {
        _keys[key.KeyId] = key;
        return Task.CompletedTask;
    }

    public Task DeactivateKeyAsync(string keyId, CancellationToken ct = default)
    {
        if (_keys.TryGetValue(keyId, out var key))
            key.IsActive = false;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string keyId, CancellationToken ct = default)
    {
        _keys.TryRemove(keyId, out _);
        return Task.CompletedTask;
    }
}

public sealed class InMemorySsoDomainStore : ISsoDomainStore
{
    private readonly ConcurrentDictionary<string, SsoDomain> _domains = new(StringComparer.OrdinalIgnoreCase);

    public Task<SsoDomain?> GetAsync(string domain, CancellationToken ct = default)
        => Task.FromResult(_domains.GetValueOrDefault(domain.ToLowerInvariant()));

    public Task<IReadOnlyList<SsoDomain>> GetAllAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<SsoDomain>>(_domains.Values.ToList());

    public Task UpsertAsync(SsoDomain domain, CancellationToken ct = default)
    {
        _domains[domain.Domain.ToLowerInvariant()] = domain;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string domain, CancellationToken ct = default)
    {
        _domains.TryRemove(domain.ToLowerInvariant(), out _);
        return Task.CompletedTask;
    }

    public Task DeleteByConnectionAsync(string connectionId, CancellationToken ct = default)
    {
        foreach (var key in _domains.Where(kvp => kvp.Value.ConnectionId == connectionId).Select(kvp => kvp.Key))
            _domains.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}

public sealed class InMemorySamlProviderStore : ISamlProviderStore
{
    private readonly ConcurrentDictionary<string, SamlProviderConfig> _providers = new();

    public Task<SamlProviderConfig?> GetAsync(string connectionId, CancellationToken ct = default)
        => Task.FromResult(_providers.GetValueOrDefault(connectionId));

    public Task<IReadOnlyList<SamlProviderConfig>> GetAllAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<SamlProviderConfig>>(_providers.Values.ToList());

    public Task UpsertAsync(SamlProviderConfig config, CancellationToken ct = default)
    {
        _providers[config.ConnectionId] = config;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string connectionId, CancellationToken ct = default)
    {
        _providers.TryRemove(connectionId, out _);
        return Task.CompletedTask;
    }
}

public sealed class InMemoryOidcProviderStore : IOidcProviderStore
{
    private readonly ConcurrentDictionary<string, OidcProviderConfig> _providers = new();

    public Task<OidcProviderConfig?> GetAsync(string connectionId, CancellationToken ct = default)
        => Task.FromResult(_providers.GetValueOrDefault(connectionId));

    public Task<IReadOnlyList<OidcProviderConfig>> GetAllAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<OidcProviderConfig>>(_providers.Values.ToList());

    public Task UpsertAsync(OidcProviderConfig config, CancellationToken ct = default)
    {
        _providers[config.ConnectionId] = config;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string connectionId, CancellationToken ct = default)
    {
        _providers.TryRemove(connectionId, out _);
        return Task.CompletedTask;
    }
}

public sealed class InMemoryUserProvisionStore : IUserProvisionStore
{
    private readonly ConcurrentDictionary<string, UserProvision> _provisions = new(); // key: userId|appId

    public Task<IReadOnlyList<UserProvision>> GetByUserAsync(string userId, CancellationToken ct = default)
    {
        var provisions = _provisions.Values.Where(p => p.UserId == userId).ToList();
        return Task.FromResult<IReadOnlyList<UserProvision>>(provisions);
    }

    public Task StoreAsync(UserProvision provision, CancellationToken ct = default)
    {
        _provisions[$"{provision.UserId}|{provision.AppId}"] = provision;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string userId, string appId, CancellationToken ct = default)
    {
        _provisions.TryRemove($"{userId}|{appId}", out _);
        return Task.CompletedTask;
    }

    public Task RemoveAllByUserAsync(string userId, CancellationToken ct = default)
    {
        foreach (var key in _provisions.Where(kvp => kvp.Value.UserId == userId).Select(kvp => kvp.Key))
            _provisions.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}
