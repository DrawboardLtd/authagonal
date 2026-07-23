using System.Security.Cryptography;
using System.Text;
using Authagonal.AwsProvider.Dynamo;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;

namespace Authagonal.AwsProvider.Stores;

/// <summary>DynamoDB implementation of <see cref="IUpstreamRefreshTokenStore"/>. One item per federated
/// session: PK = userId (env-partitioned), SK = "urt#" + hash(connectionId|sessionId). The token is
/// encrypted at rest via the shared field cipher; a <c>ttl</c> attribute lets DynamoDB auto-reap abandoned
/// rows when TTL is enabled on the table (expiry is also enforced on read regardless).</summary>
public sealed class DynamoUpstreamRefreshTokenStore(
    DynamoTable table,
    EnvPartitioner partitioner,
    IFieldCipher? fieldCipher = null) : IUpstreamRefreshTokenStore
{
    private readonly IFieldCipher _cipher = fieldCipher ?? NullFieldCipher.Instance;

    private string Pk(string userId) => partitioner.PK(userId);

    private static string Sk(string connectionId, string sessionId)
        => "urt#" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{connectionId} {sessionId}")));

    public async Task SetAsync(string userId, string connectionId, string sessionId, string refreshToken, DateTimeOffset expiresAt, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(refreshToken))
            return;
        var item = Dyn.Item(Pk(userId), Sk(connectionId, sessionId));
        item.PutS("token", await _cipher.ProtectAsync(refreshToken, ct).ConfigureAwait(false));
        item.PutN("exp", expiresAt.ToUnixTimeSeconds());
        item.PutN("ttl", expiresAt.ToUnixTimeSeconds()); // DynamoDB TTL attribute (if enabled on the table)
        await table.PutAsync(item, ct).ConfigureAwait(false);
    }

    public async Task<string?> GetAsync(string userId, string connectionId, string sessionId, CancellationToken ct = default)
    {
        var item = await table.GetAsync(Pk(userId), Sk(connectionId, sessionId), ct).ConfigureAwait(false);
        if (item is null)
            return null;
        var exp = item.GetN("exp");
        if (exp > 0 && DateTimeOffset.FromUnixTimeSeconds(exp) < DateTimeOffset.UtcNow)
            return null;
        var token = item.GetS("token");
        return string.IsNullOrEmpty(token) ? null : await _cipher.ResolveAsync(token, ct).ConfigureAwait(false);
    }

    public Task RemoveAsync(string userId, string connectionId, string sessionId, CancellationToken ct = default)
        => table.DeleteAsync(Pk(userId), Sk(connectionId, sessionId), ct);
}
