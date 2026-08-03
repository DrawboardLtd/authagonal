using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using Authagonal.Core.Models;
using Authagonal.Core.Stores;
using Authagonal.Core.Services;
using Authagonal.AzureProvider.Entities;

namespace Authagonal.AzureProvider.Stores;

public sealed class TableClientStore(TableClient clientsTable, EnvPartitioner partitioner, IChangeWriter? tombstoneWriter = null) : IClientStore
{
    public async Task<OAuthClient?> GetAsync(string clientId, CancellationToken ct = default)
    {
        try
        {
            var response = await clientsTable.GetEntityAsync<ClientEntity>(
                partitioner.PK(clientId), ClientEntity.ConfigRowKey, cancellationToken: ct);
            return response.Value.ToModel();
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<OAuthClient>> GetAllAsync(CancellationToken ct = default)
    {
        var results = new List<OAuthClient>();
        var range = partitioner.RangeForEnv();
        var query = range is null
            ? clientsTable.QueryAsync<ClientEntity>(
                e => e.RowKey == ClientEntity.ConfigRowKey, cancellationToken: ct)
            : clientsTable.QueryAsync<ClientEntity>(
                e => e.PartitionKey.CompareTo(range.Value.Low) >= 0
                     && e.PartitionKey.CompareTo(range.Value.High) < 0
                     && e.RowKey == ClientEntity.ConfigRowKey,
                cancellationToken: ct);

        await foreach (var entity in query)
        {
            results.Add(entity.ToModel());
        }

        return results;
    }

    public async Task UpsertAsync(OAuthClient client, CancellationToken ct = default)
    {
        var entity = ClientEntity.FromModel(client);
        entity.PartitionKey = partitioner.PK(entity.PartitionKey);
        await clientsTable.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);
    }

    /// <summary>
    /// ETag compare-and-set over the whole row, so an administrative write landing mid-upgrade wins.
    /// </summary>
    /// <remarks>
    /// The ETag covers the ENTIRE entity, which is exactly the property needed here: the upgrade rewrites one
    /// hash entry, and any concurrent change to any other column — <c>Enabled</c>, scopes, a secret rotation —
    /// moves the ETag and loses the race, leaving the legacy hash in place for the next attempt. See
    /// <see cref="IClientStore.TryUpgradeSecretHashAsync"/> for what the unconditional write cost.
    /// </remarks>
    public async Task<bool> TryUpgradeSecretHashAsync(
        string clientId, int index, string expectedHash, string newHash, CancellationToken ct = default)
    {
        try
        {
            var response = await clientsTable.GetEntityAsync<ClientEntity>(
                partitioner.PK(clientId), ClientEntity.ConfigRowKey, cancellationToken: ct);
            var entity = response.Value;

            var hashes = entity.ToModel().ClientSecretHashes;
            if (index >= hashes.Count) return false;
            if (!string.Equals(hashes[index], expectedHash, StringComparison.Ordinal)) return false;

            var upgraded = new List<string>(hashes) { [index] = newHash };
            entity.ClientSecretHashesJson = JsonSerializer.Serialize(
                upgraded, AzureJsonContext.Default.ListString);

            // The ETag from the read above: any write in between makes this fail rather than overwrite.
            await clientsTable.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Replace, ct);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status is 404 or 412)
        {
            // 404: the client was deleted. 412: someone else wrote first — theirs stands.
            return false;
        }
    }

    public async Task DeleteAsync(string clientId, CancellationToken ct = default)
    {
        var pk = partitioner.PK(clientId);
        if (tombstoneWriter is not null)
            await tombstoneWriter.WriteAsync("Clients", pk, ClientEntity.ConfigRowKey, ct);
        try
        {
            await clientsTable.DeleteEntityAsync(pk, ClientEntity.ConfigRowKey, cancellationToken: ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { }
    }
}
