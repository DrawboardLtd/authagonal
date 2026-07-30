using Azure.Data.Tables;
using Microsoft.Extensions.Options;

namespace Authagonal.Server.Services.Saml;

public sealed class SamlReplayCache(TableClient tableClient, IOptions<CacheOptions> cacheOptions) : Authagonal.Core.Services.ISamlReplayCache
{

    /// <summary>
    /// Stores a SAML AuthnRequest ID associated with a connection ID for later validation.
    /// </summary>
    public Task StoreRequestIdAsync(string requestId, string connectionId, CancellationToken ct = default)
        => StoreRequestAsync(requestId, connectionId, returnUrl: null, ct);

    /// <summary>
    /// Stores a SAML AuthnRequest ID with its connection ID and post-login return URL (F56: the
    /// return URL rides this row instead of RelayState, which the spec caps at 80 bytes).
    /// </summary>
    public async Task StoreRequestAsync(string requestId, string connectionId, string? returnUrl, CancellationToken ct = default)
    {
        var entity = new TableEntity(requestId, "request")
        {
            ["ConnectionId"] = connectionId,
            ["CreatedAt"] = DateTimeOffset.UtcNow
        };
        if (!string.IsNullOrEmpty(returnUrl))
            entity["ReturnUrl"] = returnUrl;

        await tableClient.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);
    }

    /// <summary>
    /// Validates that a request ID was previously stored and has not expired.
    /// Consumes the entry (deletes it) to prevent replay attacks.
    /// Returns the connection ID if valid, null otherwise.
    /// </summary>
    public async Task<string?> ValidateAndConsumeAsync(string requestId, CancellationToken ct = default)
        => (await ValidateAndConsumeRequestAsync(requestId, ct))?.ConnectionId;

    public async Task<Authagonal.Core.Services.SamlRequestState?> ValidateAndConsumeRequestAsync(string requestId, CancellationToken ct = default)
    {
        try
        {
            var response = await tableClient.GetEntityAsync<TableEntity>(
                requestId, "request", cancellationToken: ct);

            var entity = response.Value;

            // Delete immediately to prevent replay
            await tableClient.DeleteEntityAsync(requestId, "request", cancellationToken: ct);

            // Check age
            if (entity.TryGetValue("CreatedAt", out var createdAtObj) &&
                createdAtObj is DateTimeOffset createdAt)
            {
                if (DateTimeOffset.UtcNow - createdAt > TimeSpan.FromMinutes(cacheOptions.Value.SamlReplayLifetimeMinutes))
                    return null; // Expired
            }
            else
            {
                return null; // Missing timestamp — treat as invalid
            }

            var connectionId = entity.GetString("ConnectionId");
            return connectionId is null
                ? null
                : new Authagonal.Core.Services.SamlRequestState(connectionId, entity.GetString("ReturnUrl"));
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return null; // Not found — possibly replayed or never stored
        }
    }

    /// <summary>
    /// Checks whether a SAML assertion ID has been seen before (replay detection).
    /// Stores the assertion ID with a TTL to prevent replay. Returns true if the
    /// assertion is new (not replayed); false if it was already seen.
    /// </summary>
    /// <remarks>
    /// <paramref name="retainUntil"/> is accepted but unused: these rows are never expired, so retention
    /// already satisfies SAML 2.0 Profiles §4.1.4.5 for any assertion lifetime.
    /// </remarks>
    public async Task<bool> CheckAndStoreAssertionIdAsync(
        string assertionId, DateTimeOffset? retainUntil = null, CancellationToken ct = default)
    {
        var entity = new TableEntity(assertionId, "assertion")
        {
            ["CreatedAt"] = DateTimeOffset.UtcNow
        };

        try
        {
            // Add — fails with 409 Conflict if the entity already exists
            await tableClient.AddEntityAsync(entity, ct);
            return true; // New assertion — not a replay
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 409)
        {
            return false; // Already seen — replay detected
        }
    }
}
