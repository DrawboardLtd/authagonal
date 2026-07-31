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

    /// <summary>
    /// Insert only if no item with this key exists; returns false when one already does. The DynamoDB
    /// analog of Azure's <c>AddEntityAsync</c> (409), for records that must never be silently replaced.
    /// </summary>
    public async Task<bool> PutIfAbsentAsync(Dictionary<string, AttributeValue> item, CancellationToken ct = default)
    {
        try
        {
            await db.PutItemAsync(new PutItemRequest
            {
                TableName = name,
                Item = item,
                ConditionExpression = "attribute_not_exists(pk)",
            }, ct).ConfigureAwait(false);
            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            return false;
        }
    }

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

    /// <summary>
    /// Writes <paramref name="item"/> only if the stored row still carries <paramref name="expectedVersion"/>
    /// in its <c>_v</c> attribute. Returns false when another writer got there first.
    /// </summary>
    /// <remarks>
    /// The version attribute was written on every user document and tested by nothing, so the
    /// "optimistic concurrency on full-document writes" the store documents did not exist: two
    /// concurrent updates both read, both wrote, and the second silently discarded the first. On a
    /// user record that means a password reset, a deactivation or a security-stamp rotation could be
    /// erased by a racing profile write — which is the same class of defect as the Azure store's
    /// missing ETag, fixed earlier in this review.
    /// </remarks>
    public async Task<bool> PutIfVersionAsync(
        Dictionary<string, AttributeValue> item, long expectedVersion, CancellationToken ct = default)
    {
        try
        {
            await db.PutItemAsync(new PutItemRequest
            {
                TableName = name,
                Item = item,
                ConditionExpression = "attribute_not_exists(pk) OR #v = :expected",
                ExpressionAttributeNames = new Dictionary<string, string> { ["#v"] = "_v" },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":expected"] = new() { N = expectedVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                },
            }, ct).ConfigureAwait(false);
            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            return false;
        }
    }

    // ── queries ──

    /// <summary>
    /// Query a single partition. <paramref name="sortKeyCondition"/> is an optional sort-key clause
    /// (e.g. <c>"sk &gt; :cursor"</c> or <c>"sk &lt;= :hi"</c> or <c>"begins_with(sk, :p)"</c>);
    /// <paramref name="filterExpression"/> is an optional post-filter on non-key attributes. Pass any
    /// placeholders used by either through <paramref name="values"/>. Pages automatically.
    /// </summary>
    /// <param name="consistentRead">
    /// Request a strongly-consistent read. DynamoDB defaults Query to EVENTUALLY consistent, which
    /// "might not reflect the results of a recently completed write" — while
    /// <see cref="GetAsync"/> here has always set ConsistentRead, so the omission was an oversight
    /// rather than a considered trade-off. It matters for the query-driven bulk paths: enumerating a
    /// subject's grants to revoke them deletes only what the query returned, so on this backend
    /// "revoke every grant for this subject" was best-effort, where Azure and SQL are exact. Costs
    /// 2x RCU on the queries that ask for it, all of them low-frequency.
    /// </param>
    public async IAsyncEnumerable<Dictionary<string, AttributeValue>> QueryAsync(
        string pk,
        string? sortKeyCondition = null,
        string? filterExpression = null,
        IReadOnlyDictionary<string, AttributeValue>? values = null,
        bool consistentRead = false,
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
                ConsistentRead = consistentRead,
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

    /// <summary>
    /// Full-table scan returning only the projected attributes — for whole-population sweeps
    /// (id enumeration, login-state streaming) that must not read the document payload.
    /// <paramref name="names"/> aliases reserved attribute names used in the projection/filter
    /// (e.g. <c>#d → data</c>).
    /// </summary>
    public async IAsyncEnumerable<Dictionary<string, AttributeValue>> ScanProjectedAsync(
        string projection,
        string? filterExpression = null,
        IReadOnlyDictionary<string, AttributeValue>? values = null,
        IReadOnlyDictionary<string, string>? names = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        Dictionary<string, AttributeValue>? startKey = null;
        do
        {
            var resp = await db.ScanAsync(new ScanRequest
            {
                TableName = name,
                ProjectionExpression = projection,
                FilterExpression = filterExpression,
                ExpressionAttributeValues = values is null ? null : new Dictionary<string, AttributeValue>(values),
                ExpressionAttributeNames = names is null ? null : new Dictionary<string, string>(names),
                ExclusiveStartKey = startKey,
            }, ct).ConfigureAwait(false);

            foreach (var item in resp.Items ?? []) yield return item;
            startKey = resp.LastEvaluatedKey is { Count: > 0 } ? resp.LastEvaluatedKey : null;
        }
        while (startKey is not null);
    }

    /// <summary>
    /// One scan page with an explicit resume key — the native-continuation primitive behind
    /// cursor paging (<c>IUserStore.ListPageAsync</c>). <paramref name="limit"/> caps items
    /// EXAMINED (pre-filter), per DynamoDB semantics; the caller loops pages as needed.
    /// </summary>
    public async Task<(IReadOnlyList<Dictionary<string, AttributeValue>> Items, Dictionary<string, AttributeValue>? LastKey)> ScanPageAsync(
        string? filterExpression,
        IReadOnlyDictionary<string, AttributeValue>? values,
        Dictionary<string, AttributeValue>? exclusiveStartKey,
        int limit,
        CancellationToken ct = default)
    {
        var resp = await db.ScanAsync(new ScanRequest
        {
            TableName = name,
            FilterExpression = filterExpression,
            ExpressionAttributeValues = values is null ? null : new Dictionary<string, AttributeValue>(values),
            ExclusiveStartKey = exclusiveStartKey,
            Limit = limit,
        }, ct).ConfigureAwait(false);

        var lastKey = resp.LastEvaluatedKey is { Count: > 0 } ? resp.LastEvaluatedKey : null;
        return (resp.Items ?? [], lastKey);
    }

    private static Dictionary<string, AttributeValue> KeyOf(string pk, string sk) => new()
    {
        [Dyn.Pk] = new AttributeValue { S = pk },
        [Dyn.Sk] = new AttributeValue { S = sk },
    };
}
