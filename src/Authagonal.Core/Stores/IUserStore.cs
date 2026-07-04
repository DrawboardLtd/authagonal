using System.Runtime.CompilerServices;
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

    /// <summary>
    /// Record a successful login: reset the lockout counter, stamp the login time, and optionally store a
    /// rehashed password. This exists as a separate method from <see cref="UpdateAsync"/> so an encrypting
    /// store can persist ONLY these plaintext auth columns without decrypting and re-encrypting every PII
    /// field (which a full update does) just to write a timestamp on the hottest path. The default is the
    /// straightforward read-modify-write; encrypting backends override it to skip the crypto round-trips.
    /// </summary>
    async Task RecordSuccessfulLoginAsync(string userId, string? rehashedPassword = null, CancellationToken ct = default)
    {
        var user = await GetAsync(userId, ct);
        if (user is null) return;
        user.AccessFailedCount = 0;
        user.LockoutEnd = null;
        user.LastLoginAt = DateTimeOffset.UtcNow;
        user.UpdatedAt = DateTimeOffset.UtcNow;
        if (rehashedPassword is not null) user.PasswordHash = rehashedPassword;
        await UpdateAsync(user, ct);
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

    /// <summary>
    /// Re-write one user to the current at-rest scheme: re-encrypt the profile's PII and rewrite the
    /// profile-derived index rows (email, domain, first/last name) under the current keys, removing any
    /// legacy-keyed rows. Idempotent — the cold-row backfill for enabling encryption. Default is a no-op.
    /// </summary>
    Task ReindexUserAsync(string userId, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>
    /// Stream every user's id for this store, cheaply: id-only (no PII decryption) and paged via the
    /// backend's native continuation, so it is O(N) rather than the O(N²) offset re-scan of
    /// <see cref="ListAsync"/> that also decrypts every skipped row. Used by the cold-row encryption
    /// backfill, which only needs ids to feed <see cref="ReindexUserAsync"/>. Default yields nothing.
    /// </summary>
    async IAsyncEnumerable<string> EnumerateUserIdsAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    Task SetExternalIdAsync(string userId, string clientId, string externalId, CancellationToken ct = default);
    Task RemoveExternalIdAsync(string userId, string clientId, string externalId, CancellationToken ct = default);

    Task AddLoginAsync(ExternalLoginInfo login, CancellationToken ct = default);
    Task RemoveLoginAsync(string userId, string provider, string providerKey, CancellationToken ct = default);
    Task<ExternalLoginInfo?> FindLoginAsync(string provider, string providerKey, CancellationToken ct = default);
    Task<IReadOnlyList<ExternalLoginInfo>> GetLoginsAsync(string userId, CancellationToken ct = default);
}
