using Authagonal.Core.Services;
using Azure.Data.Tables;
using Microsoft.Extensions.Options;

namespace Authagonal.Server.Services.Oidc;

public sealed class OidcStateStore(TableClient tableClient, IOptions<CacheOptions> cacheOptions) : IOidcStateStore
{

    /// <summary>
    /// Stores OIDC authorization state keyed by the state parameter for later validation during callback.
    /// </summary>
    public async Task StoreAsync(
        string state,
        string connectionId,
        string returnUrl,
        string codeVerifier,
        string nonce,
        CancellationToken ct = default)
    {
        var entity = new TableEntity(state, "state")
        {
            ["ConnectionId"] = connectionId,
            ["ReturnUrl"] = returnUrl,
            ["CodeVerifier"] = codeVerifier,
            ["Nonce"] = nonce,
            ["CreatedAt"] = DateTimeOffset.UtcNow
        };

        await tableClient.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);
    }

    /// <summary>
    /// Consumes (reads and deletes) the state entry. Returns null if not found or expired.
    /// </summary>
    public async Task<OidcStateData?> ConsumeAsync(string state, CancellationToken ct = default)
    {
        try
        {
            var response = await tableClient.GetEntityAsync<TableEntity>(
                state, "state", cancellationToken: ct);

            var entity = response.Value;

            // Atomic single-use (F48a): only the caller whose ETag-conditional delete removes the row may
            // proceed; a concurrent callback presenting the same state loses (412/404) and gets null.
            // Prevents two callbacks from both consuming one federation state.
            try
            {
                await tableClient.DeleteEntityAsync(state, "state", entity.ETag, ct);
            }
            catch (Azure.RequestFailedException ex) when (ex.Status is 412 or 404)
            {
                return null;
            }

            // Check age
            if (entity.TryGetValue("CreatedAt", out var createdAtObj) &&
                createdAtObj is DateTimeOffset createdAt)
            {
                if (DateTimeOffset.UtcNow - createdAt > TimeSpan.FromMinutes(cacheOptions.Value.OidcStateLifetimeMinutes))
                    return null; // Expired
            }
            else
            {
                return null; // Missing timestamp -- treat as invalid
            }

            var connectionId = entity.GetString("ConnectionId");
            var returnUrl = entity.GetString("ReturnUrl");
            var codeVerifier = entity.GetString("CodeVerifier");
            var nonce = entity.GetString("Nonce");

            if (connectionId is null || returnUrl is null || codeVerifier is null || nonce is null)
                return null;

            return new OidcStateData(connectionId, returnUrl, codeVerifier, nonce);
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return null; // Not found
        }
    }
}
