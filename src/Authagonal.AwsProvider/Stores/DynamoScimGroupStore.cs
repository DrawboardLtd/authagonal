using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using Authagonal.AwsProvider.Dynamo;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;

namespace Authagonal.AwsProvider.Stores;

/// <summary>DynamoDB <see cref="IScimGroupStore"/>. Primary group rows (pk = groupId, sk = "group")
/// plus an external-id index (pk = "{orgId}|{externalId}", sk = "group-lookup"). Membership lookups
/// scan and filter in-process, mirroring the Azure store (members live inside the group document).</summary>
public sealed class DynamoScimGroupStore(
    DynamoTable groups,
    DynamoTable groupExternalIds,
    EnvPartitioner partitioner,
    IChangeWriter? tombstones = null) : IScimGroupStore
{
    private const string GroupSk = "group";
    private const string GroupLookupSk = "group-lookup";

    public async Task<ScimGroup?> GetAsync(string groupId, CancellationToken ct = default)
    {
        var item = await groups.GetAsync(partitioner.PK(groupId), GroupSk, ct).ConfigureAwait(false);
        return item is null ? null : Read(item);
    }

    public async Task<ScimGroup?> FindByExternalIdAsync(string organizationId, string externalId, CancellationToken ct = default)
    {
        var idx = await groupExternalIds.GetAsync(partitioner.PK($"{organizationId}|{externalId}"), GroupLookupSk, ct).ConfigureAwait(false);
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
        await groups.PutAsync(GroupItem(group), ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(group.ExternalId) && !string.IsNullOrEmpty(group.OrganizationId))
            await groupExternalIds.PutAsync(ExternalIdItem(group.OrganizationId, group.ExternalId, group.Id), ct).ConfigureAwait(false);
    }

    public async Task UpdateAsync(ScimGroup group, CancellationToken ct = default)
    {
        var existing = await groups.GetAsync(partitioner.PK(group.Id), GroupSk, ct).ConfigureAwait(false);
        if (existing is null)
        {
            await CreateAsync(group, ct).ConfigureAwait(false);
            return;
        }

        var old = Read(existing);
        await groups.PutAsync(GroupItem(group), ct).ConfigureAwait(false);

        // Drop a stale external-id index entry if the (org, externalId) pair changed.
        if (!string.IsNullOrEmpty(old.ExternalId) && !string.IsNullOrEmpty(old.OrganizationId) &&
            (!string.Equals(old.ExternalId, group.ExternalId, StringComparison.Ordinal) ||
             !string.Equals(old.OrganizationId, group.OrganizationId, StringComparison.Ordinal)))
        {
            await DeleteExternalIdAsync(old.OrganizationId, old.ExternalId, ct).ConfigureAwait(false);
        }

        if (!string.IsNullOrEmpty(group.ExternalId) && !string.IsNullOrEmpty(group.OrganizationId))
            await groupExternalIds.PutAsync(ExternalIdItem(group.OrganizationId, group.ExternalId, group.Id), ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string groupId, CancellationToken ct = default)
    {
        var groupPk = partitioner.PK(groupId);
        var existing = await groups.GetAsync(groupPk, GroupSk, ct).ConfigureAwait(false);
        if (existing is null) return;

        var group = Read(existing);
        if (!string.IsNullOrEmpty(group.ExternalId) && !string.IsNullOrEmpty(group.OrganizationId))
            await DeleteExternalIdAsync(group.OrganizationId, group.ExternalId, ct).ConfigureAwait(false);

        await groups.DeleteAsync(groupPk, GroupSk, ct).ConfigureAwait(false);
        if (tombstones is not null) await tombstones.WriteAsync("ScimGroups", groupPk, GroupSk, ct).ConfigureAwait(false);
    }

    private async IAsyncEnumerable<ScimGroup> ScanGroupsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var (filter, values) = DynamoClientStore.ConfigScanFilter(partitioner, GroupSk);
        await foreach (var item in groups.ScanAsync(filter, values, ct).ConfigureAwait(false))
            yield return Read(item);
    }

    private async Task DeleteExternalIdAsync(string organizationId, string externalId, CancellationToken ct)
    {
        var pk = partitioner.PK($"{organizationId}|{externalId}");
        await groupExternalIds.DeleteAsync(pk, GroupLookupSk, ct).ConfigureAwait(false);
        if (tombstones is not null) await tombstones.WriteAsync("ScimGroupExternalIds", pk, GroupLookupSk, ct).ConfigureAwait(false);
    }

    private Dictionary<string, AttributeValue> GroupItem(ScimGroup group)
    {
        var item = Dyn.Item(partitioner.PK(group.Id), GroupSk);
        item.PutS("data", JsonSerializer.Serialize(group, AwsJsonContext.Default.ScimGroup));
        return item;
    }

    private Dictionary<string, AttributeValue> ExternalIdItem(string organizationId, string externalId, string groupId)
    {
        var item = Dyn.Item(partitioner.PK($"{organizationId}|{externalId}"), GroupLookupSk);
        item.PutS("groupId", groupId);
        return item;
    }

    private static ScimGroup Read(Dictionary<string, AttributeValue> item)
        => JsonSerializer.Deserialize(item.GetStr("data"), AwsJsonContext.Default.ScimGroup)!;
}
