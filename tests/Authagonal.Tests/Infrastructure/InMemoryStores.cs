using System.Collections.Concurrent;
using System.Security.Cryptography;
using Authagonal.Core.Models;
using Authagonal.Core.Stores;

namespace Authagonal.Tests.Infrastructure;

public sealed class InMemoryUserStore : IUserStore
{
    private readonly ConcurrentDictionary<string, AuthUser> _users = new();
    private readonly ConcurrentDictionary<string, ExternalLoginInfo> _logins = new(); // key: provider|providerKey
    private readonly ConcurrentDictionary<string, string> _externalIds = new(); // key: clientId|externalId -> userId

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

    public Task<AuthUser?> FindByExternalIdAsync(string clientId, string externalId, CancellationToken ct = default)
    {
        if (_externalIds.TryGetValue($"{clientId}|{externalId}", out var userId))
            return Task.FromResult(_users.GetValueOrDefault(userId));
        return Task.FromResult<AuthUser?>(null);
    }

    public Task<(IReadOnlyList<AuthUser> Users, int TotalCount)> ListAsync(string? organizationId, int startIndex, int count, CancellationToken ct = default)
    {
        var all = _users.Values.AsEnumerable();
        if (organizationId is not null)
            all = all.Where(u => u.OrganizationId == organizationId);

        var list = all.OrderBy(u => u.CreatedAt).ToList();
        var paged = list.Skip(startIndex - 1).Take(count).ToList();
        return Task.FromResult<(IReadOnlyList<AuthUser>, int)>((paged, list.Count));
    }

    public Task SetExternalIdAsync(string userId, string clientId, string externalId, CancellationToken ct = default)
    {
        _externalIds[$"{clientId}|{externalId}"] = userId;
        return Task.CompletedTask;
    }

    public Task RemoveExternalIdAsync(string userId, string clientId, string externalId, CancellationToken ct = default)
    {
        _externalIds.TryRemove($"{clientId}|{externalId}", out _);
        return Task.CompletedTask;
    }

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

public sealed class InMemoryMfaStore : IMfaStore
{
    private readonly ConcurrentDictionary<string, MfaCredential> _credentials = new(); // key: userId|credentialId
    private readonly ConcurrentDictionary<string, MfaChallenge> _challenges = new();    // key: challengeId
    private readonly ConcurrentDictionary<string, (string UserId, string CredentialId)> _webAuthnIndex = new(); // key: sha256(webAuthnCredId) hex

    public Task<IReadOnlyList<MfaCredential>> GetCredentialsAsync(string userId, CancellationToken ct = default)
    {
        var creds = _credentials.Values.Where(c => c.UserId == userId).ToList();
        return Task.FromResult<IReadOnlyList<MfaCredential>>(creds);
    }

    public Task<MfaCredential?> GetCredentialAsync(string userId, string credentialId, CancellationToken ct = default)
        => Task.FromResult(_credentials.GetValueOrDefault($"{userId}|{credentialId}"));

    public Task CreateCredentialAsync(MfaCredential credential, CancellationToken ct = default)
    {
        _credentials[$"{credential.UserId}|{credential.Id}"] = credential;
        return Task.CompletedTask;
    }

    public Task UpdateCredentialAsync(MfaCredential credential, CancellationToken ct = default)
    {
        _credentials[$"{credential.UserId}|{credential.Id}"] = credential;
        return Task.CompletedTask;
    }

    public Task DeleteCredentialAsync(string userId, string credentialId, CancellationToken ct = default)
    {
        _credentials.TryRemove($"{userId}|{credentialId}", out _);
        return Task.CompletedTask;
    }

    public Task DeleteAllCredentialsAsync(string userId, CancellationToken ct = default)
    {
        foreach (var key in _credentials.Where(kvp => kvp.Value.UserId == userId).Select(kvp => kvp.Key))
            _credentials.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public Task<(string UserId, string CredentialId)?> FindByWebAuthnCredentialIdAsync(byte[] webAuthnCredentialId, CancellationToken ct = default)
    {
        var hash = HashWebAuthnCredentialId(webAuthnCredentialId);
        if (_webAuthnIndex.TryGetValue(hash, out var mapping))
            return Task.FromResult<(string, string)?>(mapping);
        return Task.FromResult<(string UserId, string CredentialId)?>(null);
    }

    public Task StoreWebAuthnCredentialIdMappingAsync(byte[] webAuthnCredentialId, string userId, string credentialId, CancellationToken ct = default)
    {
        var hash = HashWebAuthnCredentialId(webAuthnCredentialId);
        _webAuthnIndex[hash] = (userId, credentialId);
        return Task.CompletedTask;
    }

    public Task DeleteWebAuthnCredentialIdMappingAsync(byte[] webAuthnCredentialId, CancellationToken ct = default)
    {
        var hash = HashWebAuthnCredentialId(webAuthnCredentialId);
        _webAuthnIndex.TryRemove(hash, out _);
        return Task.CompletedTask;
    }

    public Task StoreChallengeAsync(MfaChallenge challenge, CancellationToken ct = default)
    {
        _challenges[challenge.ChallengeId] = challenge;
        return Task.CompletedTask;
    }

    public Task<MfaChallenge?> GetChallengeAsync(string challengeId, CancellationToken ct = default)
    {
        if (!_challenges.TryGetValue(challengeId, out var challenge))
            return Task.FromResult<MfaChallenge?>(null);

        if (challenge.IsConsumed || challenge.ExpiresAt <= DateTimeOffset.UtcNow)
            return Task.FromResult<MfaChallenge?>(null);

        return Task.FromResult<MfaChallenge?>(challenge);
    }

    public Task<MfaChallenge?> ConsumeChallengeAsync(string challengeId, CancellationToken ct = default)
    {
        if (!_challenges.TryRemove(challengeId, out var challenge))
            return Task.FromResult<MfaChallenge?>(null);

        if (challenge.IsConsumed || challenge.ExpiresAt <= DateTimeOffset.UtcNow)
            return Task.FromResult<MfaChallenge?>(null);

        return Task.FromResult<MfaChallenge?>(challenge);
    }

    private static string HashWebAuthnCredentialId(byte[] credentialId)
    {
        var hash = SHA256.HashData(credentialId);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

public sealed class InMemoryScimTokenStore : IScimTokenStore
{
    private readonly ConcurrentDictionary<string, ScimToken> _byHash = new(); // key: tokenHash
    private readonly ConcurrentDictionary<string, ScimToken> _byId = new();   // key: clientId|tokenId

    public Task<ScimToken?> FindByHashAsync(string tokenHash, CancellationToken ct = default)
        => Task.FromResult(_byHash.GetValueOrDefault(tokenHash));

    public Task<IReadOnlyList<ScimToken>> GetByClientAsync(string clientId, CancellationToken ct = default)
    {
        var tokens = _byId.Values.Where(t => t.ClientId == clientId).ToList();
        return Task.FromResult<IReadOnlyList<ScimToken>>(tokens);
    }

    public Task StoreAsync(ScimToken token, CancellationToken ct = default)
    {
        _byHash[token.TokenHash] = token;
        _byId[$"{token.ClientId}|{token.TokenId}"] = token;
        return Task.CompletedTask;
    }

    public Task RevokeAsync(string tokenId, string clientId, CancellationToken ct = default)
    {
        if (_byId.TryGetValue($"{clientId}|{tokenId}", out var token))
        {
            token.IsRevoked = true;
        }
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string tokenId, string clientId, CancellationToken ct = default)
    {
        if (_byId.TryRemove($"{clientId}|{tokenId}", out var token))
        {
            _byHash.TryRemove(token.TokenHash, out _);
        }
        return Task.CompletedTask;
    }
}

public sealed class InMemoryScimGroupStore : IScimGroupStore
{
    private readonly ConcurrentDictionary<string, ScimGroup> _groups = new();

    public Task<ScimGroup?> GetAsync(string groupId, CancellationToken ct = default)
        => Task.FromResult(_groups.GetValueOrDefault(groupId));

    public Task<ScimGroup?> FindByExternalIdAsync(string organizationId, string externalId, CancellationToken ct = default)
    {
        var group = _groups.Values.FirstOrDefault(g =>
            g.OrganizationId == organizationId && g.ExternalId == externalId);
        return Task.FromResult(group);
    }

    public Task<(IReadOnlyList<ScimGroup> Groups, int TotalCount)> ListAsync(string? organizationId, int startIndex, int count, CancellationToken ct = default)
    {
        var all = _groups.Values.AsEnumerable();
        if (organizationId is not null)
            all = all.Where(g => g.OrganizationId == organizationId);

        var list = all.OrderBy(g => g.CreatedAt).ToList();
        var paged = list.Skip(startIndex - 1).Take(count).ToList();
        return Task.FromResult<(IReadOnlyList<ScimGroup>, int)>((paged, list.Count));
    }

    public Task<IReadOnlyList<ScimGroup>> GetGroupsByUserIdAsync(string userId, CancellationToken ct = default)
    {
        var groups = _groups.Values
            .Where(g => g.MemberUserIds.Contains(userId))
            .ToList();
        return Task.FromResult<IReadOnlyList<ScimGroup>>(groups);
    }

    public Task CreateAsync(ScimGroup group, CancellationToken ct = default)
    {
        _groups[group.Id] = group;
        return Task.CompletedTask;
    }

    public Task UpdateAsync(ScimGroup group, CancellationToken ct = default)
    {
        _groups[group.Id] = group;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string groupId, CancellationToken ct = default)
    {
        _groups.TryRemove(groupId, out _);
        return Task.CompletedTask;
    }
}

public sealed class InMemoryRoleStore : IRoleStore
{
    private readonly ConcurrentDictionary<string, Role> _roles = new();

    public Task<Role?> GetAsync(string roleId, CancellationToken ct = default)
        => Task.FromResult(_roles.GetValueOrDefault(roleId));

    public Task<Role?> GetByNameAsync(string name, CancellationToken ct = default)
    {
        var role = _roles.Values.FirstOrDefault(r =>
            string.Equals(r.Name, name, StringComparison.Ordinal));
        return Task.FromResult(role);
    }

    public Task<IReadOnlyList<Role>> ListAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Role>>(_roles.Values.OrderBy(r => r.CreatedAt).ToList());

    public Task CreateAsync(Role role, CancellationToken ct = default)
    {
        _roles[role.Id] = role;
        return Task.CompletedTask;
    }

    public Task UpdateAsync(Role role, CancellationToken ct = default)
    {
        _roles[role.Id] = role;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string roleId, CancellationToken ct = default)
    {
        _roles.TryRemove(roleId, out _);
        return Task.CompletedTask;
    }
}
