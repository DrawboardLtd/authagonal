using Authagonal.AwsProvider.Dynamo;
using Authagonal.Core.Services;

namespace Authagonal.AwsProvider.Stores;

/// <summary>DynamoDB <see cref="IOidcStateStore"/>. pk = state, sk = "state"; the state is consumed
/// (conditional delete) on callback so it's strictly single-use.</summary>
public sealed class DynamoOidcStateStore(DynamoTable table, TimeSpan ttl) : IOidcStateStore
{
    private const string StateSk = "state";

    public Task StoreAsync(string state, string connectionId, string returnUrl, string codeVerifier, string nonce, CancellationToken ct = default)
    {
        var item = Dyn.Item(state, StateSk);
        item.PutS("connectionId", connectionId);
        item.PutS("returnUrl", returnUrl);
        item.PutS("codeVerifier", codeVerifier);
        item.PutS("nonce", nonce);
        item.PutDate("createdAt", DateTimeOffset.UtcNow);
        return table.PutAsync(item, ct);
    }

    public async Task<OidcStateData?> ConsumeAsync(string state, CancellationToken ct = default)
    {
        var old = await table.DeleteIfExistsReturningAsync(state, StateSk, ct).ConfigureAwait(false);
        if (old is null) return null; // not found / already consumed
        if (DateTimeOffset.UtcNow - old.GetDate("createdAt") > ttl) return null; // expired

        var connectionId = old.GetS("connectionId");
        var returnUrl = old.GetS("returnUrl");
        var codeVerifier = old.GetS("codeVerifier");
        var nonce = old.GetS("nonce");
        if (connectionId is null || returnUrl is null || codeVerifier is null || nonce is null) return null;

        return new OidcStateData(connectionId, returnUrl, codeVerifier, nonce);
    }
}
