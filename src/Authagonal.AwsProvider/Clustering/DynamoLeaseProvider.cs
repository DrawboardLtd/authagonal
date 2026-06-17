using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Authagonal.Core.Clustering;
using Microsoft.Extensions.Logging;

namespace Authagonal.AwsProvider.Clustering;

/// <summary>
/// Leader-election lease backed by a DynamoDB conditional write — the AWS counterpart to the Azure
/// blob lease. One item per resource (pk = "lease#{resource}", sk = "lease") holds {owner, expiresAt}.
/// Acquire/renew is a single conditional <c>PutItem</c> that succeeds only when the lease is unheld,
/// has expired, or is already ours, giving at-most-one-holder semantics without a native lease
/// primitive (DynamoDB has none).
///
/// Expiry is compared against each writer's own clock versus the stored <c>expiresAt</c>; with a TTL
/// well above realistic inter-node clock skew this is safe, and a brief overlap at most delays a single
/// renewal — it never hands the lease to two holders, because the conditional write is atomic.
/// </summary>
public sealed class DynamoLeaseProvider(IAmazonDynamoDB db, string tableName, ILogger<DynamoLeaseProvider> logger) : ILeaseProvider
{
    public async Task<bool> TryAcquireOrRenewAsync(string resource, string nodeId, TimeSpan ttl, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var nowMs = now.ToUnixTimeMilliseconds();
        var expiresMs = now.Add(ttl).ToUnixTimeMilliseconds();

        try
        {
            await db.PutItemAsync(new PutItemRequest
            {
                TableName = tableName,
                Item = new Dictionary<string, AttributeValue>
                {
                    ["pk"] = new() { S = LeaseKey(resource) },
                    ["sk"] = new() { S = "lease" },
                    ["owner"] = new() { S = nodeId },
                    ["expiresAt"] = new() { N = expiresMs.ToString(CultureInfo.InvariantCulture) },
                },
                // Acquire if there's no row, the existing lease has expired, or we already hold it (renew).
                ConditionExpression = "attribute_not_exists(pk) OR #e < :now OR #o = :me",
                ExpressionAttributeNames = new Dictionary<string, string> { ["#o"] = "owner", ["#e"] = "expiresAt" },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":now"] = new() { N = nowMs.ToString(CultureInfo.InvariantCulture) },
                    [":me"] = new() { S = nodeId },
                },
            }, ct).ConfigureAwait(false);
            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            return false; // another node holds a live lease
        }
    }

    public async Task ReleaseAsync(string resource, string nodeId, CancellationToken ct = default)
    {
        try
        {
            await db.DeleteItemAsync(new DeleteItemRequest
            {
                TableName = tableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    ["pk"] = new() { S = LeaseKey(resource) },
                    ["sk"] = new() { S = "lease" },
                },
                ConditionExpression = "#o = :me",
                ExpressionAttributeNames = new Dictionary<string, string> { ["#o"] = "owner" },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":me"] = new() { S = nodeId } },
            }, ct).ConfigureAwait(false);
        }
        catch (ConditionalCheckFailedException)
        {
            // Already taken over by another node — nothing for us to release.
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Lease release failed for {Resource}", resource);
        }
    }

    private static string LeaseKey(string resource) => $"lease#{resource}";
}
