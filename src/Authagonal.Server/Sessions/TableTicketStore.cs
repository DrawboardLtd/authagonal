using System.Security.Claims;
using Authagonal.Core.Services;
using Azure;
using Azure.Data.Tables;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;

namespace Authagonal.Server;

/// <summary>
/// Single-tenant server-side storage for the auth host's cookie SSO session (an <see cref="ITicketStore"/>).
/// The signed cookie carries only an opaque session id; the authentication ticket lives in a
/// <c>Sessions</c> table, so sessions are <b>instantly revocable</b> (delete the row and the session is dead
/// on the next request) and enumerable per user via a <c>SessionsByUser</c> index. Also implements
/// <see cref="IUserSessionRegistry"/> so the login SPA's "active sessions" section + the
/// <c>/api/auth/sessions</c> endpoints light up. The multi-tenant cloud has its own tenant-sharded
/// equivalent; this is the flavour a self-hosted single-tenant host uses.
/// </summary>
internal sealed class TableTicketStore(
    TableClient sessions,
    TableClient sessionsByUser,
    EnvPartitioner partitioner,
    IHttpContextAccessor httpContextAccessor) : ITicketStore, IUserSessionRegistry
{
    internal const string Partition = "session";

    private string SessionPk => partitioner.PK(Partition);
    private string UserPk(string userId) => partitioner.PK(userId);

    public async Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        var key = Guid.NewGuid().ToString("N");
        await WriteAsync(key, ticket, isNew: true);
        return key;
    }

    // Renew keeps the same key: refresh the ticket + touch last-seen, but preserve the created-at.
    public Task RenewAsync(string key, AuthenticationTicket ticket) => WriteAsync(key, ticket, isNew: false);

    public async Task<AuthenticationTicket?> RetrieveAsync(string key)
    {
        try
        {
            var response = await sessions.GetEntityAsync<SessionEntity>(SessionPk, key);
            var ticket = TicketSerializer.Default.Deserialize(Convert.FromBase64String(response.Value.Data));
            if (ticket?.Properties.ExpiresUtc is { } expires && expires < DateTimeOffset.UtcNow)
            {
                await RemoveAsync(key);
                return null;
            }
            // Stash the current session id under the shared library convention so the self-service
            // endpoints can flag / protect "this session".
            if (httpContextAccessor.HttpContext is { } hc) hc.Items[IUserSessionRegistry.CurrentSessionItem] = key;
            return ticket;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task RemoveAsync(string key)
    {
        // Read the row first to learn the user id, so the index row (PK = userId) can also be removed.
        string? userId = null;
        try { userId = (await sessions.GetEntityAsync<SessionEntity>(SessionPk, key)).Value.UserId; }
        catch (RequestFailedException ex) when (ex.Status == 404) { /* already gone */ }

        // Both deletes swallow 404: a double logout / revoke race (or an already-swept expired row) must
        // not surface as a 500 on the cookie-auth hot path, and must not abort a bulk RevokeOthersAsync.
        await DeleteIfExistsAsync(sessions, SessionPk, key);
        if (!string.IsNullOrEmpty(userId))
            await DeleteIfExistsAsync(sessionsByUser, UserPk(userId), key);
    }

    private static async Task DeleteIfExistsAsync(TableClient table, string pk, string rowKey)
    {
        try { await table.DeleteEntityAsync(pk, rowKey, ETag.All); }
        catch (RequestFailedException ex) when (ex.Status == 404) { /* already gone */ }
    }

    private async Task WriteAsync(string key, AuthenticationTicket ticket, bool isNew)
    {
        var userId = ticket.Principal.FindFirst("sub")?.Value
            ?? ticket.Principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? "";
        var now = DateTimeOffset.UtcNow;

        await sessions.UpsertEntityAsync(new SessionEntity
        {
            PartitionKey = SessionPk,
            RowKey = key,
            Data = Convert.ToBase64String(TicketSerializer.Default.Serialize(ticket)),
            UserId = userId,
            ExpiresUtc = ticket.Properties.ExpiresUtc,
            IssuedUtc = ticket.Properties.IssuedUtc,
        }, TableUpdateMode.Replace);

        if (string.IsNullOrEmpty(userId)) return;

        var ctx = httpContextAccessor.HttpContext;
        var indexEntity = new SessionIndexEntity
        {
            PartitionKey = UserPk(userId),
            RowKey = key,
            LastSeenAt = now,
            ExpiresUtc = ticket.Properties.ExpiresUtc,
            Ip = ctx?.Connection.RemoteIpAddress?.ToString() ?? "",
            UserAgent = Truncate(ctx?.Request.Headers.UserAgent.ToString() ?? "", 512),
        };
        if (isNew) indexEntity.CreatedAt = now;

        // Merge on renew preserves CreatedAt (set only on the first store); Replace on the first store
        // establishes the row.
        await sessionsByUser.UpsertEntityAsync(indexEntity, isNew ? TableUpdateMode.Replace : TableUpdateMode.Merge);

        // A new session (login) is the trigger to reap this user's expired ones — cheap, no background
        // job, and off the per-request renew path.
        if (isNew) await SweepExpiredForUserAsync(userId);
    }

    // Lazily reap a user's expired sessions when they establish a new one — Azure Table has no native
    // TTL, so abandoned sessions would otherwise accumulate forever in both tables. Best-effort: a sweep
    // failure never blocks the login that triggered it.
    private async Task SweepExpiredForUserAsync(string userId)
    {
        try
        {
            var pk = UserPk(userId);
            var now = DateTimeOffset.UtcNow;
            var expired = new List<string>();
            await foreach (var e in sessionsByUser.QueryAsync<SessionIndexEntity>(x => x.PartitionKey == pk))
                if (e.ExpiresUtc is { } exp && exp < now) expired.Add(e.RowKey);
            foreach (var sid in expired) await RemoveAsync(sid);
        }
        catch (RequestFailedException)
        {
            // Opportunistic — the next login retries.
        }
    }

    // --- IUserSessionRegistry: self-service listing / revocation for the current user ---

    public async Task<IReadOnlyList<SessionDescriptor>> ListAsync(string userId, string? currentSessionId, CancellationToken ct = default)
    {
        var pk = UserPk(userId);
        var results = new List<SessionDescriptor>();
        await foreach (var e in sessionsByUser.QueryAsync<SessionIndexEntity>(x => x.PartitionKey == pk, cancellationToken: ct))
        {
            // Don't surface an expired session as active — Azure Table has no TTL, so a stale index row
            // can outlive its session until the lazy sweep (SweepExpiredForUserAsync) reaps it.
            if (e.ExpiresUtc is { } exp && exp < DateTimeOffset.UtcNow) continue;
            results.Add(new SessionDescriptor(
                e.RowKey,
                currentSessionId is not null && e.RowKey == currentSessionId,
                e.CreatedAt ?? e.LastSeenAt,
                e.LastSeenAt,
                e.ExpiresUtc,
                e.Ip,
                e.UserAgent));
        }
        return results.OrderByDescending(s => s.LastSeenAt).ToList();
    }

    public async Task<bool> RevokeAsync(string userId, string sessionId, CancellationToken ct = default)
    {
        // Verify the session belongs to this user (via the index) before killing it.
        try { _ = await sessionsByUser.GetEntityAsync<SessionIndexEntity>(UserPk(userId), sessionId, cancellationToken: ct); }
        catch (RequestFailedException ex) when (ex.Status == 404) { return false; }
        await RemoveAsync(sessionId);
        return true;
    }

    public async Task<int> RevokeOthersAsync(string userId, string? keepSessionId, CancellationToken ct = default)
    {
        var pk = UserPk(userId);
        var toRemove = new List<string>();
        await foreach (var e in sessionsByUser.QueryAsync<SessionIndexEntity>(x => x.PartitionKey == pk, cancellationToken: ct))
            if (e.RowKey != keepSessionId) toRemove.Add(e.RowKey);
        foreach (var sid in toRemove) await RemoveAsync(sid);
        return toRemove.Count;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}

/// <summary>A stored SSO session: the serialized auth ticket. RowKey is the opaque session id in the cookie.</summary>
internal sealed class SessionEntity : ITableEntity
{
    public string PartitionKey { get; set; } = TableTicketStore.Partition;
    public string RowKey { get; set; } = "";
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }
    public string Data { get; set; } = "";
    public string UserId { get; set; } = "";
    public DateTimeOffset? ExpiresUtc { get; set; }
    public DateTimeOffset? IssuedUtc { get; set; }
}

/// <summary>Per-user session index for listing + revocation. PK = userId so a user's sessions are a single
/// point query; RowKey is the session id. Carries display metadata (never the ticket itself).</summary>
internal sealed class SessionIndexEntity : ITableEntity
{
    public string PartitionKey { get; set; } = "";
    public string RowKey { get; set; } = "";
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }
    /// <summary>Nullable so that on a Merge (session renew) it is OMITTED from the payload rather than sent
    /// as <c>DateTimeOffset.MinValue</c> (0001-01-01), which Azure Table rejects as OutOfRangeInput; the
    /// existing value is preserved. Set only on the first (Replace) store.</summary>
    public DateTimeOffset? CreatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset? ExpiresUtc { get; set; }
    public string Ip { get; set; } = "";
    public string UserAgent { get; set; } = "";
}
