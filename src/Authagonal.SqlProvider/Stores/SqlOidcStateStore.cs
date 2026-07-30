using Authagonal.Core.Services;
using Authagonal.SqlProvider.Sql;

namespace Authagonal.SqlProvider.Stores;

/// <summary>
/// SQL <see cref="IOidcStateStore"/>. pk = state, sk = "state"; the state is consumed
/// (delete-and-return) on callback so it is strictly single-use, and the row carries a TTL so states
/// abandoned mid-flow are reaped rather than accumulating.
/// </summary>
public sealed class SqlOidcStateStore(SqlTable table, TimeSpan ttl) : IOidcStateStore
{
    private const string StateSk = "state";

    public Task StoreAsync(
        string state, string connectionId, string returnUrl, string codeVerifier, string nonce, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var row = new SqlRow(state, StateSk) { ExpiresAt = now.Add(ttl) };
        row.PutS("connectionId", connectionId);
        row.PutS("returnUrl", returnUrl);
        row.PutS("codeVerifier", codeVerifier);
        row.PutS("nonce", nonce);
        row.PutDate("createdAt", now);
        return table.PutAsync(row, ct);
    }

    public async Task<OidcStateData?> ConsumeAsync(string state, CancellationToken ct = default)
    {
        var old = await table.DeleteIfExistsReturningAsync(state, StateSk, ct).ConfigureAwait(false);
        if (old is null) return null;                                            // not found / already consumed
        if (DateTimeOffset.UtcNow - old.GetDate("createdAt") > ttl) return null;  // expired

        var connectionId = old.GetS("connectionId");
        var returnUrl = old.GetS("returnUrl");
        var codeVerifier = old.GetS("codeVerifier");
        var nonce = old.GetS("nonce");
        if (connectionId is null || returnUrl is null || codeVerifier is null || nonce is null) return null;

        return new OidcStateData(connectionId, returnUrl, codeVerifier, nonce);
    }
}
