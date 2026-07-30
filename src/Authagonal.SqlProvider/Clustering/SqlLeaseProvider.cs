using Authagonal.Core.Clustering;
using Authagonal.SqlProvider.Sql;
using Microsoft.Extensions.Logging;

namespace Authagonal.SqlProvider.Clustering;

/// <summary>
/// Leader-election lease backed by a conditional upsert — the SQL counterpart to the Azure blob lease
/// and the DynamoDB conditional write. One row per resource (pk = "lease#{resource}", sk = "lease")
/// holds the owner and its expiry. Acquire/renew is a single statement whose <c>DO UPDATE … WHERE</c>
/// admits only an expired lease or the current holder, giving at-most-one-holder semantics without a
/// native lease primitive.
/// <para>
/// Expiry is compared against each writer's own clock; with a TTL well above realistic inter-node
/// clock skew that is safe, and a brief overlap at most delays a single renewal — it never hands the
/// lease to two holders, because the statement is atomic.
/// </para>
/// </summary>
public sealed class SqlLeaseProvider(SqlTable table, ILogger<SqlLeaseProvider> logger) : ILeaseProvider
{
    private const string LeaseSk = "lease";

    public Task<bool> TryAcquireOrRenewAsync(string resource, string nodeId, TimeSpan ttl, CancellationToken ct = default)
        => table.TryAcquireLeaseAsync(LeaseKey(resource), LeaseSk, nodeId, DateTimeOffset.UtcNow.Add(ttl), ct);

    public async Task ReleaseAsync(string resource, string nodeId, CancellationToken ct = default)
    {
        try
        {
            // Conditional on still being the owner: a node that already took over must not have its
            // lease deleted by the previous holder's late release.
            await table.DeleteIfAttrEqualsAsync(LeaseKey(resource), LeaseSk, "owner", nodeId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Lease release failed for {Resource}", resource);
        }
    }

    private static string LeaseKey(string resource) => $"lease#{resource}";
}
