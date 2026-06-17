using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using Authagonal.AwsProvider.Dynamo;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;

namespace Authagonal.AwsProvider.Stores;

/// <summary>DynamoDB <see cref="IScopeStore"/>. All scopes share one partition ("scope"), sk = scope name.</summary>
public sealed class DynamoScopeStore(DynamoTable table, EnvPartitioner partitioner, ITombstoneWriter? tombstones = null) : IScopeStore
{
    private const string Partition = "scope";

    public async Task<Scope?> GetAsync(string name, CancellationToken ct = default)
    {
        var item = await table.GetAsync(partitioner.PK(Partition), name, ct).ConfigureAwait(false);
        return item is null ? null : Read(item);
    }

    public async Task<IReadOnlyList<Scope>> ListAsync(CancellationToken ct = default)
    {
        var scopes = new List<Scope>();
        await foreach (var item in table.QueryAsync(partitioner.PK(Partition), ct: ct).ConfigureAwait(false))
            scopes.Add(Read(item));
        return scopes;
    }

    public Task CreateAsync(Scope scope, CancellationToken ct = default) => Write(scope, ct);
    public Task UpdateAsync(Scope scope, CancellationToken ct = default) => Write(scope, ct);

    public async Task DeleteAsync(string name, CancellationToken ct = default)
    {
        var pk = partitioner.PK(Partition);
        var old = await table.DeleteIfExistsReturningAsync(pk, name, ct).ConfigureAwait(false);
        if (old is not null && tombstones is not null) await tombstones.WriteAsync("Scopes", pk, name, ct).ConfigureAwait(false);
    }

    private Task Write(Scope scope, CancellationToken ct)
    {
        var item = Dyn.Item(partitioner.PK(Partition), scope.Name);
        item.PutS("data", JsonSerializer.Serialize(scope, AwsJsonContext.Default.Scope));
        return table.PutAsync(item, ct);
    }

    private static Scope Read(Dictionary<string, AttributeValue> item)
        => JsonSerializer.Deserialize(item.GetStr("data"), AwsJsonContext.Default.Scope)!;
}
