using Amazon.DynamoDBv2.Model;
using Authagonal.AwsProvider.Dynamo;
using Authagonal.Core.Services;

namespace Authagonal.AwsProvider.Stores;

/// <summary>DynamoDB <see cref="ISamlReplayCache"/>. One table: pk = request/assertion id, sk =
/// "request" | "assertion". Request ids are consumed (conditional delete) for single-use validation;
/// assertion ids are recorded via a conditional put so a second sighting is detected as a replay.</summary>
public sealed class DynamoSamlReplayCache(DynamoTable table, TimeSpan ttl) : ISamlReplayCache
{
    private const string RequestSk = "request";
    private const string AssertionSk = "assertion";

    public Task StoreRequestIdAsync(string requestId, string connectionId, CancellationToken ct = default)
    {
        var item = Dyn.Item(requestId, RequestSk);
        item.PutS("connectionId", connectionId);
        item.PutDate("createdAt", DateTimeOffset.UtcNow);
        return table.PutAsync(item, ct);
    }

    public async Task<string?> ValidateAndConsumeAsync(string requestId, CancellationToken ct = default)
    {
        var old = await table.DeleteIfExistsReturningAsync(requestId, RequestSk, ct).ConfigureAwait(false);
        if (old is null) return null; // not found / already consumed (replay)
        if (DateTimeOffset.UtcNow - old.GetDate("createdAt") > ttl) return null; // expired
        return old.GetS("connectionId");
    }

    public async Task<bool> CheckAndStoreAssertionIdAsync(string assertionId, CancellationToken ct = default)
    {
        var item = Dyn.Item(assertionId, AssertionSk);
        item.PutDate("createdAt", DateTimeOffset.UtcNow);
        try
        {
            await table.Client.PutItemAsync(new PutItemRequest
            {
                TableName = table.Name,
                Item = item,
                ConditionExpression = "attribute_not_exists(pk)",
            }, ct).ConfigureAwait(false);
            return true; // first sighting — not a replay
        }
        catch (ConditionalCheckFailedException)
        {
            return false; // already seen — replay detected
        }
    }
}
