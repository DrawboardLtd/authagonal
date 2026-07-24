using Amazon.DynamoDBv2.Model;
using Authagonal.AwsProvider.Dynamo;
using Authagonal.Core.Authority;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;

namespace Authagonal.AwsProvider.Stores;

/// <summary>
/// DynamoDB <see cref="IAgentProfileStore"/>. All profiles share one partition ("agent"),
/// sk = clientId; the ceiling rides as its RFC 9396 JSON string.
/// </summary>
public sealed class DynamoAgentProfileStore(
    DynamoTable table,
    EnvPartitioner partitioner,
    IChangeWriter? tombstones = null) : IAgentProfileStore
{
    private const string Partition = "agent";

    public async Task<AgentProfile?> GetAsync(string clientId, CancellationToken ct = default)
    {
        var item = await table.GetAsync(partitioner.PK(Partition), clientId, ct).ConfigureAwait(false);
        return item is null ? null : Read(clientId, item);
    }

    public async Task<IReadOnlyList<AgentProfile>> GetAllAsync(CancellationToken ct = default)
    {
        var profiles = new List<AgentProfile>();
        await foreach (var item in table.QueryAsync(partitioner.PK(Partition), ct: ct).ConfigureAwait(false))
            profiles.Add(Read(item.GetStr(Dyn.Sk), item));
        return profiles;
    }

    public async Task UpsertAsync(AgentProfile profile, CancellationToken ct = default)
    {
        var item = Dyn.Item(partitioner.PK(Partition), profile.ClientId);
        item.PutS("mode", AgentModes.Name(profile.Mode));
        item.PutS("ceiling", AuthorityJson.Serialize(profile.Ceiling));
        item.PutN("maxDelegationDepth", profile.MaxDelegationDepth);
        item.PutN("maxTokenLifetimeSeconds", profile.MaxTokenLifetimeSeconds);
        item.PutS("highRiskDefault", AuthorityJson.PolicyName(profile.HighRiskDefault));
        item.PutDate("createdAt", profile.CreatedAt);
        item.PutDate("updatedAt", profile.UpdatedAt);
        await table.PutAsync(item, ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string clientId, CancellationToken ct = default)
    {
        var pk = partitioner.PK(Partition);
        var old = await table.DeleteIfExistsReturningAsync(pk, clientId, ct).ConfigureAwait(false);
        if (old is not null && tombstones is not null)
            await tombstones.WriteAsync("AgentProfiles", pk, clientId, ct).ConfigureAwait(false);
    }

    private static AgentProfile Read(string clientId, Dictionary<string, AttributeValue> item) => new()
    {
        ClientId = clientId,
        Mode = AgentModes.Parse(item.GetS("mode")),
        // A garbled ceiling reads as Empty — corruption must never widen a grant.
        Ceiling = AuthorityJson.TryParse(item.GetStr("ceiling"), out var ceiling) ? ceiling : AuthoritySet.Empty,
        MaxDelegationDepth = (int)item.GetN("maxDelegationDepth"),
        MaxTokenLifetimeSeconds = (int)item.GetN("maxTokenLifetimeSeconds"),
        HighRiskDefault = AuthorityJson.ParsePolicyName(item.GetS("highRiskDefault")),
        CreatedAt = item.GetDate("createdAt"),
        UpdatedAt = item.GetDateOrNull("updatedAt"),
    };
}
