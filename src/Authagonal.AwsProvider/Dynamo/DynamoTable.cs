using System.Runtime.CompilerServices;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace Authagonal.AwsProvider.Dynamo;

/// <summary>
/// Thin wrapper over a single DynamoDB table, giving the Authagonal stores the same ergonomics as
/// Azure's <c>TableClient</c>. Every item is keyed by <c>pk</c> (HASH) + <c>sk</c> (RANGE). Handles
/// the v4-SDK detail that response collections may be null, and transparently pages queries/scans.
/// </summary>
public sealed class DynamoTable(IAmazonDynamoDB db, string name)
{
    /// <summary>The underlying client, for the few callers that need bespoke conditional expressions.</summary>
    public IAmazonDynamoDB Client => db;

    public string Name => name;

    // ── reads ──

    /// <summary>Strongly-consistent point read. Returns null when the item is absent.</summary>
    public async Task<Dictionary<string, AttributeValue>?> GetAsync(string pk, string sk, CancellationToken ct = default)
    {
        var resp = await db.GetItemAsync(new GetItemRequest
        {
            TableName = name,
            Key = KeyOf(pk, sk),
            ConsistentRead = true,
        }, ct).ConfigureAwait(false);
        return resp.Item is { Count: > 0 } ? resp.Item : null;
    }

    // ── writes ──

    /// <summary>Upsert (replace).</summary>
    public Task PutAsync(Dictionary<string, AttributeValue> item, CancellationToken ct = default)
        => db.PutItemAsync(new PutItemRequest { TableName = name, Item = item }, ct);

    // ── deletes ──

    /// <summary>Unconditional delete; succeeds even if the item is already gone.</summary>
    public Task DeleteAsync(string pk, string sk, CancellationToken ct = default)
        => db.DeleteItemAsync(new DeleteItemRequest { TableName = name, Key = KeyOf(pk, sk) }, ct);

    /// <summary>
    /// Atomic single-use delete: removes the item only if it currently exists and returns the removed
    /// attributes; returns null if it was already gone. This is the DynamoDB analog of Azure's
    /// conditional (ETag) delete that gives single-use grants their anti-replay guarantee — exactly one
    /// concurrent caller can win.
    /// </summary>
    public async Task<Dictionary<string, AttributeValue>?> DeleteIfExistsReturningAsync(string pk, string sk, CancellationToken ct = default)
    {
        try
        {
            var resp = await db.DeleteItemAsync(new DeleteItemRequest
            {
                TableName = name,
                Key = KeyOf(pk, sk),
                ConditionExpression = "attribute_exists(pk)",
                ReturnValues = ReturnValue.ALL_OLD,
            }, ct).ConfigureAwait(false);
            return resp.Attributes is { Count: > 0 } ? resp.Attributes : null;
        }
        catch (ConditionalCheckFailedException)
        {
            return null;
        }
    }

    // ── queries ──

    /// <summary>
    /// Query a single partition. <paramref name="sortKeyCondition"/> is an optional sort-key clause
    /// (e.g. <c>"sk &gt; :cursor"</c> or <c>"sk &lt;= :hi"</c> or <c>"begins_with(sk, :p)"</c>);
    /// <paramref name="filterExpression"/> is an optional post-filter on non-key attributes. Pass any
    /// placeholders used by either through <paramref name="values"/>. Pages automatically.
    /// </summary>
    public async IAsyncEnumerable<Dictionary<string, AttributeValue>> QueryAsync(
        string pk,
        string? sortKeyCondition = null,
        string? filterExpression = null,
        IReadOnlyDictionary<string, AttributeValue>? values = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var keyExpr = sortKeyCondition is null ? "pk = :pk" : $"pk = :pk AND {sortKeyCondition}";
        var attrValues = new Dictionary<string, AttributeValue> { [":pk"] = new() { S = pk } };
        if (values is not null)
            foreach (var kv in values) attrValues[kv.Key] = kv.Value;

        Dictionary<string, AttributeValue>? startKey = null;
        do
        {
            var resp = await db.QueryAsync(new QueryRequest
            {
                TableName = name,
                KeyConditionExpression = keyExpr,
                FilterExpression = filterExpression,
                ExpressionAttributeValues = attrValues,
                ExclusiveStartKey = startKey,
            }, ct).ConfigureAwait(false);

            foreach (var item in resp.Items ?? []) yield return item;
            startKey = resp.LastEvaluatedKey is { Count: > 0 } ? resp.LastEvaluatedKey : null;
        }
        while (startKey is not null);
    }

    /// <summary>
    /// Full-table scan, for the handful of low-frequency "list all in env" admin operations whose
    /// Azure counterparts also scan (they range over PartitionKey, which DynamoDB cannot key-query).
    /// </summary>
    public async IAsyncEnumerable<Dictionary<string, AttributeValue>> ScanAsync(
        string? filterExpression = null,
        IReadOnlyDictionary<string, AttributeValue>? values = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        Dictionary<string, AttributeValue>? startKey = null;
        do
        {
            var resp = await db.ScanAsync(new ScanRequest
            {
                TableName = name,
                FilterExpression = filterExpression,
                ExpressionAttributeValues = values is null ? null : new Dictionary<string, AttributeValue>(values),
                ExclusiveStartKey = startKey,
            }, ct).ConfigureAwait(false);

            foreach (var item in resp.Items ?? []) yield return item;
            startKey = resp.LastEvaluatedKey is { Count: > 0 } ? resp.LastEvaluatedKey : null;
        }
        while (startKey is not null);
    }

    private static Dictionary<string, AttributeValue> KeyOf(string pk, string sk) => new()
    {
        [Dyn.Pk] = new AttributeValue { S = pk },
        [Dyn.Sk] = new AttributeValue { S = sk },
    };
}
