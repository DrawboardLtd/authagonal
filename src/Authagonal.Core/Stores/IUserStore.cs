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

    /// <summary>
    /// Cursor-paged user list (F26): resumes from an opaque continuation token instead of an offset,
    /// so page N of a large tenant costs one storage page — offset paging re-enumerates (and, with
    /// field encryption on, re-DECRYPTS) every skipped row on every page, quadratic over a pagination
    /// pass. <paramref name="count"/> is a page-size hint, not a guarantee (a server-filtered page
    /// may return fewer, or slightly more when short pages are coalesced). A null
    /// <see cref="UserPage.ContinuationToken"/> means the listing is exhausted. Default: emulates
    /// over the offset <see cref="ListAsync"/> (token = numeric index) for non-table stores;
    /// table-backed stores override with native continuation tokens.
    /// </summary>
    async Task<UserPage> ListPageAsync(string? organizationId, int count, string? continuationToken, CancellationToken ct = default)
    {
        var start = int.TryParse(continuationToken, out var s) ? s : 0;
        var (users, hasMore) = await ListAsync(organizationId, start, count, ct);
        return new UserPage(users, hasMore ? (start + users.Count).ToString() : null);
    }

    /// <summary>Cursor-paged variant of <see cref="ListByScimClientAsync"/> — see
    /// <see cref="ListPageAsync"/> for the token contract.</summary>
    async Task<UserPage> ListByScimClientPageAsync(string scimClientId, int count, string? continuationToken, CancellationToken ct = default)
    {
        var start = int.TryParse(continuationToken, out var s) ? s : 0;
        var (users, hasMore) = await ListByScimClientAsync(scimClientId, start, count, ct);
        return new UserPage(users, hasMore ? (start + users.Count).ToString() : null);
    }
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

    /// <summary>
    /// Re-key legacy plaintext-keyed <c>UserExternalIds</c> forward-index rows to blind-index tokens.
    /// Unlike the profile-derived indexes (email/domain/name), externalId rows are NOT reachable from
    /// <see cref="ReindexUserAsync"/> — there is no userId→externalId reverse index — so migrating them
    /// requires a standalone table scan. Idempotent (already-tokenized rows are skipped); write-before-delete
    /// keeps lookups live. No-op when tokenization is off. Returns the number of legacy rows found
    /// (<paramref name="dryRun"/>) or migrated (live run) — a live-run 0 means the index is fully tokenized.
    /// Default: no-op (non-tokenizing / non-table stores).
    /// </summary>
    Task<int> MigrateExternalIdIndexAsync(bool dryRun, CancellationToken ct = default) => Task.FromResult(0);

    /// <summary>
    /// Re-key + encrypt legacy plaintext <c>UserLogins</c> rows (forward lookup + reverse per-user list) to
    /// the blind-index scheme: HMAC-token lookup keys, encrypted ProviderKey/DisplayName columns. Like
    /// <see cref="MigrateExternalIdIndexAsync"/> it is a standalone table scan (no reverse index to drive a
    /// per-user rewrite), write-before-delete, idempotent. Returns rows found (<paramref name="dryRun"/>) /
    /// migrated (live run). Default: no-op.
    /// </summary>
    Task<int> MigrateUserLoginsAsync(bool dryRun, CancellationToken ct = default) => Task.FromResult(0);

    Task SetExternalIdAsync(string userId, string clientId, string externalId, CancellationToken ct = default);
    Task RemoveExternalIdAsync(string userId, string clientId, string externalId, CancellationToken ct = default);

    Task AddLoginAsync(ExternalLoginInfo login, CancellationToken ct = default);
    Task RemoveLoginAsync(string userId, string provider, string providerKey, CancellationToken ct = default);
    Task<ExternalLoginInfo?> FindLoginAsync(string provider, string providerKey, CancellationToken ct = default);
    Task<IReadOnlyList<ExternalLoginInfo>> GetLoginsAsync(string userId, CancellationToken ct = default);

    /// <summary>
    /// Streams every user's non-PII login-state snapshot (id, created, last login, active) — for
    /// retention-style sweeps that must evaluate the whole population without decrypting profiles or
    /// materializing the table. Default: per-id <see cref="GetAsync"/> fallback (correct but decrypts);
    /// table-backed stores override with a column-projected scan that touches no encrypted field.
    /// </summary>
    async IAsyncEnumerable<UserLoginState> EnumerateLoginStatesAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var id in EnumerateUserIdsAsync(ct))
        {
            var user = await GetAsync(id, ct);
            if (user is not null)
                yield return new UserLoginState(user.Id, user.CreatedAt, user.LastLoginAt, user.IsActive);
        }
    }
}

/// <summary>A user's non-PII login-state columns, as streamed by
/// <see cref="IUserStore.EnumerateLoginStatesAsync"/>.</summary>
public sealed record UserLoginState(string Id, DateTimeOffset CreatedAt, DateTimeOffset? LastLoginAt, bool IsActive);

/// <summary>One cursor page of users. <see cref="ContinuationToken"/> is opaque — feed it back to
/// the same List*PageAsync call verbatim; null means no further pages.</summary>
public sealed record UserPage(IReadOnlyList<AuthUser> Users, string? ContinuationToken);
