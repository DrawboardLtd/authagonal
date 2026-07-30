using System.Runtime.CompilerServices;
using System.Text.Json;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.SqlProvider.Sql;

namespace Authagonal.SqlProvider.Stores;

/// <summary>
/// SQL <see cref="IScimGroupStore"/>. Primary group rows (pk = groupId, sk = "group") plus an
/// external-id index (pk = "{orgId}|{externalId}", sk = "group-lookup"). Membership lookups scan and
/// filter in-process, mirroring the other backends (members live inside the group document).
/// </summary>
public sealed class SqlScimGroupStore(
    SqlTable groups,
    SqlTable groupExternalIds,
    EnvPartitioner partitioner,
    IChangeWriter? tombstones = null) : IScimGroupStore
{
    private const string GroupSk = "group";
    private const string GroupLookupSk = "group-lookup";

    public async Task<ScimGroup?> GetAsync(string groupId, CancellationToken ct = default)
    {
        var row = await groups.GetAsync(partitioner.PK(groupId), GroupSk, ct: ct).ConfigureAwait(false);
        return row is null ? null : Read(row);
    }

    public async Task<ScimGroup?> FindByExternalIdAsync(string organizationId, string externalId, CancellationToken ct = default)
    {
        var idx = await groupExternalIds.GetAsync(
            partitioner.PK($"{organizationId}|{externalId}"), GroupLookupSk, ct: ct).ConfigureAwait(false);
        return idx is null ? null : await GetAsync(idx.GetStr("groupId"), ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ScimGroup>> GetGroupsByUserIdAsync(string userId, CancellationToken ct = default)
    {
        var result = new List<ScimGroup>();
        await foreach (var group in ScanGroupsAsync(ct).ConfigureAwait(false))
            if (group.MemberUserIds.Contains(userId)) result.Add(group);
        return result;
    }

    public async Task<(IReadOnlyList<ScimGroup> Groups, int TotalCount)> ListAsync(
        string? organizationId, int startIndex, int count, CancellationToken ct = default)
    {
        var all = new List<ScimGroup>();
        await foreach (var group in ScanGroupsAsync(ct).ConfigureAwait(false))
            if (organizationId is null || string.Equals(group.OrganizationId, organizationId, StringComparison.Ordinal))
                all.Add(group);

        var paged = all.OrderBy(g => g.CreatedAt).Skip(startIndex - 1).Take(count).ToList();
        return (paged, all.Count);
    }

    public async Task CreateAsync(ScimGroup group, CancellationToken ct = default)
    {
        await groups.PutAsync(GroupRow(group), ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(group.ExternalId) && !string.IsNullOrEmpty(group.OrganizationId))
            await groupExternalIds.PutAsync(ExternalIdRow(group.OrganizationId, group.ExternalId, group.Id), ct).ConfigureAwait(false);
    }

    public async Task UpdateAsync(ScimGroup group, CancellationToken ct = default)
    {
        var existing = await groups.GetAsync(partitioner.PK(group.Id), GroupSk, ct: ct).ConfigureAwait(false);
        if (existing is null)
        {
            await CreateAsync(group, ct).ConfigureAwait(false);
            return;
        }

        var old = Read(existing);
        await groups.PutAsync(GroupRow(group), ct).ConfigureAwait(false);

        // Drop a stale external-id index entry if the (org, externalId) pair changed.
        if (!string.IsNullOrEmpty(old.ExternalId) && !string.IsNullOrEmpty(old.OrganizationId) &&
            (!string.Equals(old.ExternalId, group.ExternalId, StringComparison.Ordinal) ||
             !string.Equals(old.OrganizationId, group.OrganizationId, StringComparison.Ordinal)))
        {
            await DeleteExternalIdAsync(old.OrganizationId, old.ExternalId, ct).ConfigureAwait(false);
        }

        if (!string.IsNullOrEmpty(group.ExternalId) && !string.IsNullOrEmpty(group.OrganizationId))
            await groupExternalIds.PutAsync(ExternalIdRow(group.OrganizationId, group.ExternalId, group.Id), ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string groupId, CancellationToken ct = default)
    {
        var groupPk = partitioner.PK(groupId);
        var existing = await groups.GetAsync(groupPk, GroupSk, ct: ct).ConfigureAwait(false);
        if (existing is null) return;

        var group = Read(existing);
        if (!string.IsNullOrEmpty(group.ExternalId) && !string.IsNullOrEmpty(group.OrganizationId))
            await DeleteExternalIdAsync(group.OrganizationId, group.ExternalId, ct).ConfigureAwait(false);

        await groups.DeleteAsync(groupPk, GroupSk, ct).ConfigureAwait(false);
        if (tombstones is not null)
            await tombstones.WriteAsync("ScimGroups", groupPk, GroupSk, ct).ConfigureAwait(false);
    }

    private async IAsyncEnumerable<ScimGroup> ScanGroupsAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var row in groups.QueryAsync(SqlFilters.Config(partitioner, GroupSk), ct).ConfigureAwait(false))
            yield return Read(row);
    }

    private async Task DeleteExternalIdAsync(string organizationId, string externalId, CancellationToken ct)
    {
        var pk = partitioner.PK($"{organizationId}|{externalId}");
        await groupExternalIds.DeleteAsync(pk, GroupLookupSk, ct).ConfigureAwait(false);
        if (tombstones is not null)
            await tombstones.WriteAsync("ScimGroupExternalIds", pk, GroupLookupSk, ct).ConfigureAwait(false);
    }

    private SqlRow GroupRow(ScimGroup group) => new(partitioner.PK(group.Id), GroupSk)
    {
        Data = JsonSerializer.Serialize(group, SqlJsonContext.Default.ScimGroup),
    };

    private SqlRow ExternalIdRow(string organizationId, string externalId, string groupId)
    {
        var row = new SqlRow(partitioner.PK($"{organizationId}|{externalId}"), GroupLookupSk);
        row.PutS("groupId", groupId);
        return row;
    }

    private static ScimGroup Read(SqlRow row)
        => JsonSerializer.Deserialize(row.DataOrEmpty, SqlJsonContext.Default.ScimGroup)!;
}
