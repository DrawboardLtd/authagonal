using Authagonal.Core.Models;

namespace Authagonal.Core.Stores;

public interface IUserStore
{
    Task<AuthUser?> GetAsync(string userId, CancellationToken ct = default);
    Task<AuthUser?> FindByEmailAsync(string email, CancellationToken ct = default);
    Task CreateAsync(AuthUser user, CancellationToken ct = default);
    Task UpdateAsync(AuthUser user, CancellationToken ct = default);
    Task DeleteAsync(string userId, CancellationToken ct = default);

    /// <summary>
    /// Atomically record a failed login: increment AccessFailedCount and, when it reaches
    /// <paramref name="maxAttempts"/>, set the lockout window and reset the counter. Returns true if
    /// the account is now locked. Implementations should use optimistic concurrency so concurrent
    /// failures aren't lost — a plain read-modify-write lets an attacker exceed the lockout threshold
    /// with parallel requests. The default is that non-atomic read-modify-write (correct for
    /// single-process stores); durable backends override it with a conditional update + retry.
    /// </summary>
    async Task<bool> RecordFailedLoginAsync(string userId, int maxAttempts, TimeSpan lockoutDuration, CancellationToken ct = default)
    {
        var user = await GetAsync(userId, ct);
        if (user is null)
            return false;

        user.AccessFailedCount++;
        var locked = false;
        if (user.LockoutEnabled && user.AccessFailedCount >= maxAttempts)
        {
            user.LockoutEnd = DateTimeOffset.UtcNow.Add(lockoutDuration);
            user.AccessFailedCount = 0;
            locked = true;
        }
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await UpdateAsync(user, ct);
        return locked;
    }
    Task<bool> ExistsAsync(string userId, CancellationToken ct = default);

    Task<AuthUser?> FindByExternalIdAsync(string clientId, string externalId, CancellationToken ct = default);
    Task<(IReadOnlyList<AuthUser> Users, bool HasMore)> ListAsync(string? organizationId, int startIndex, int count, CancellationToken ct = default);
    Task<(IReadOnlyList<AuthUser> Users, bool HasMore)> ListByScimClientAsync(string scimClientId, int startIndex, int count, CancellationToken ct = default);
    Task<IReadOnlyList<AuthUser>> SearchAsync(string query, int maxResults = 20, CancellationToken ct = default);

    /// <summary>
    /// Find users whose email is at <paramref name="domain"/> (e.g. "acme.com") — backed by the
    /// email-domain blind index. Default returns empty for stores that don't implement it.
    /// </summary>
    Task<IReadOnlyList<AuthUser>> SearchByEmailDomainAsync(string domain, int maxResults = 50, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AuthUser>>([]);

    Task SetExternalIdAsync(string userId, string clientId, string externalId, CancellationToken ct = default);
    Task RemoveExternalIdAsync(string userId, string clientId, string externalId, CancellationToken ct = default);

    Task AddLoginAsync(ExternalLoginInfo login, CancellationToken ct = default);
    Task RemoveLoginAsync(string userId, string provider, string providerKey, CancellationToken ct = default);
    Task<ExternalLoginInfo?> FindLoginAsync(string provider, string providerKey, CancellationToken ct = default);
    Task<IReadOnlyList<ExternalLoginInfo>> GetLoginsAsync(string userId, CancellationToken ct = default);
}
