using System.Security.Cryptography;
using System.Text;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.SqlProvider.Sql;

namespace Authagonal.SqlProvider.Stores;

/// <summary>
/// SQL <see cref="IUpstreamRefreshTokenStore"/>. One row per federated session: pk = userId
/// (env-partitioned), sk = "urt#" + hash(connectionId|sessionId). The token is encrypted at rest via
/// the shared field cipher, and the row carries its expiry as a TTL so <see cref="SqlExpiryReaper"/>
/// clears abandoned sessions (expiry is enforced on read regardless).
/// </summary>
public sealed class SqlUpstreamRefreshTokenStore(
    SqlTable table,
    EnvPartitioner partitioner,
    IFieldCipher? fieldCipher = null) : IUpstreamRefreshTokenStore
{
    private readonly IFieldCipher _cipher = fieldCipher ?? NullFieldCipher.Instance;

    private string Pk(string userId) => partitioner.PK(userId);

    private static string Sk(string connectionId, string sessionId)
        => "urt#" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{connectionId} {sessionId}")));

    public async Task SetAsync(
        string userId, string connectionId, string sessionId, string refreshToken, DateTimeOffset expiresAt, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(refreshToken)) return;

        var row = new SqlRow(Pk(userId), Sk(connectionId, sessionId)) { ExpiresAt = expiresAt };
        row.PutS("token", await _cipher.ProtectAsync(refreshToken, ct).ConfigureAwait(false));
        row.PutN("exp", expiresAt.ToUnixTimeSeconds());
        await table.PutAsync(row, ct).ConfigureAwait(false);
    }

    public async Task<string?> GetAsync(string userId, string connectionId, string sessionId, CancellationToken ct = default)
    {
        var row = await table.GetAsync(Pk(userId), Sk(connectionId, sessionId), ct: ct).ConfigureAwait(false);
        if (row is null) return null;

        var exp = row.GetN("exp");
        if (exp > 0 && DateTimeOffset.FromUnixTimeSeconds(exp) < DateTimeOffset.UtcNow) return null;

        var token = row.GetS("token");
        return string.IsNullOrEmpty(token) ? null : await _cipher.ResolveAsync(token, ct).ConfigureAwait(false);
    }

    public Task RemoveAsync(string userId, string connectionId, string sessionId, CancellationToken ct = default)
        => table.DeleteAsync(Pk(userId), Sk(connectionId, sessionId), ct);
}
