using Authagonal.Core.Authority;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.SqlProvider.Sql;

namespace Authagonal.SqlProvider.Stores;

/// <summary>
/// SQL <see cref="IAgentProfileStore"/>. All profiles share one partition ("agent"), sk = clientId;
/// the ceiling rides as its RFC 9396 JSON string.
/// </summary>
public sealed class SqlAgentProfileStore(SqlTable table, EnvPartitioner partitioner, IChangeWriter? tombstones = null) : IAgentProfileStore
{
    private const string Partition = "agent";

    public async Task<AgentProfile?> GetAsync(string clientId, CancellationToken ct = default)
    {
        var row = await table.GetAsync(partitioner.PK(Partition), clientId, ct: ct).ConfigureAwait(false);
        return row is null ? null : Read(clientId, row);
    }

    public async Task<IReadOnlyList<AgentProfile>> GetAllAsync(CancellationToken ct = default)
    {
        var profiles = new List<AgentProfile>();
        await foreach (var row in table.QueryPartitionAsync(partitioner.PK(Partition), ct).ConfigureAwait(false))
            profiles.Add(Read(row.Sk, row));
        return profiles;
    }

    public async Task UpsertAsync(AgentProfile profile, CancellationToken ct = default)
    {
        var row = new SqlRow(partitioner.PK(Partition), profile.ClientId);
        row.PutS("mode", AgentModes.Name(profile.Mode));
        row.PutS("ceiling", AuthorityJson.Serialize(profile.Ceiling));
        row.PutN("maxDelegationDepth", profile.MaxDelegationDepth);
        row.PutN("maxTokenLifetimeSeconds", profile.MaxTokenLifetimeSeconds);
        row.PutS("highRiskDefault", AuthorityJson.PolicyName(profile.HighRiskDefault));
        row.PutDate("createdAt", profile.CreatedAt);
        row.PutDate("updatedAt", profile.UpdatedAt);
        await table.PutAsync(row, ct).ConfigureAwait(false);
        // Recorded, not just deletes: an incremental window that carries the deletions and none of
        // the writes reconstructs a table that is missing every row created or changed in it.
        if (tombstones is not null)
            await tombstones.WriteUpsertAsync("AgentProfiles", partitioner.PK(Partition), profile.ClientId, ct)
                .ConfigureAwait(false);
    }

    public async Task DeleteAsync(string clientId, CancellationToken ct = default)
    {
        var pk = partitioner.PK(Partition);
        var old = await table.DeleteIfExistsReturningAsync(pk, clientId, ct).ConfigureAwait(false);
        if (old is not null && tombstones is not null)
            await tombstones.WriteAsync("AgentProfiles", pk, clientId, ct).ConfigureAwait(false);
    }

    private static AgentProfile Read(string clientId, SqlRow row) => new()
    {
        ClientId = clientId,
        Mode = AgentModes.Parse(row.GetS("mode")),
        // A garbled ceiling reads as Empty — corruption must never widen a grant.
        Ceiling = AuthorityJson.TryParse(row.GetStr("ceiling"), out var ceiling) ? ceiling : AuthoritySet.Empty,
        MaxDelegationDepth = (int)row.GetN("maxDelegationDepth"),
        MaxTokenLifetimeSeconds = (int)row.GetN("maxTokenLifetimeSeconds"),
        HighRiskDefault = AuthorityJson.ParsePolicyName(row.GetS("highRiskDefault")),
        CreatedAt = row.GetDate("createdAt"),
        UpdatedAt = row.GetDateOrNull("updatedAt"),
    };
}
