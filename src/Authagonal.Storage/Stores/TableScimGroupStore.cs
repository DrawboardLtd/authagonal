using Azure;
using Azure.Data.Tables;
using Authagonal.Core.Models;
using Authagonal.Core.Stores;
using Authagonal.Core.Services;
using Authagonal.Storage.Entities;

namespace Authagonal.Storage.Stores;

public sealed class TableScimGroupStore(
    TableClient scimGroupsTable,
    TableClient scimGroupExternalIdsTable,
    ITombstoneWriter? tombstoneWriter = null) : IScimGroupStore
{
    public async Task<ScimGroup?> GetAsync(string groupId, CancellationToken ct = default)
    {
        try
        {
            var response = await scimGroupsTable.GetEntityAsync<ScimGroupEntity>(
                groupId, ScimGroupEntity.GroupRowKey, cancellationToken: ct);
            return response.Value.ToModel();
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<ScimGroup?> FindByExternalIdAsync(string organizationId, string externalId, CancellationToken ct = default)
    {
        try
        {
            var indexEntity = await scimGroupExternalIdsTable.GetEntityAsync<ScimGroupExternalIdEntity>(
                $"{organizationId}|{externalId}", ScimGroupEntity.GroupLookupRowKey, cancellationToken: ct);
            return await GetAsync(indexEntity.Value.GroupId, ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<ScimGroup>> GetGroupsByUserIdAsync(string userId, CancellationToken ct = default)
    {
        var groups = new List<ScimGroup>();
        var query = scimGroupsTable.QueryAsync<ScimGroupEntity>(
            e => e.RowKey == ScimGroupEntity.GroupRowKey,
            cancellationToken: ct);

        await foreach (var entity in query)
        {
            var group = entity.ToModel();
            if (group.MemberUserIds.Contains(userId))
            {
                groups.Add(group);
            }
        }

        return groups;
    }

    public async Task<(IReadOnlyList<ScimGroup> Groups, int TotalCount)> ListAsync(
        string? organizationId, int startIndex, int count, CancellationToken ct = default)
    {
        var allGroups = new List<ScimGroup>();
        var query = scimGroupsTable.QueryAsync<ScimGroupEntity>(
            e => e.RowKey == ScimGroupEntity.GroupRowKey,
            cancellationToken: ct);

        await foreach (var entity in query)
        {
            var group = entity.ToModel();
            if (organizationId is null || string.Equals(group.OrganizationId, organizationId, StringComparison.Ordinal))
            {
                allGroups.Add(group);
            }
        }

        var totalCount = allGroups.Count;
        var paged = allGroups
            .OrderBy(g => g.CreatedAt)
            .Skip(startIndex - 1)
            .Take(count)
            .ToList();

        return (paged, totalCount);
    }

    public async Task CreateAsync(ScimGroup group, CancellationToken ct = default)
    {
        var entity = ScimGroupEntity.FromModel(group);
        await scimGroupsTable.AddEntityAsync(entity, ct);

        if (!string.IsNullOrEmpty(group.ExternalId) && !string.IsNullOrEmpty(group.OrganizationId))
        {
            var indexEntity = ScimGroupEntity.CreateExternalIdIndex(group.OrganizationId, group.ExternalId, group.Id);
            await scimGroupExternalIdsTable.UpsertEntityAsync(indexEntity, TableUpdateMode.Replace, ct);
        }
    }

    public async Task UpdateAsync(ScimGroup group, CancellationToken ct = default)
    {
        // Fetch existing to check if externalId changed
        try
        {
            var existing = await scimGroupsTable.GetEntityAsync<ScimGroupEntity>(
                group.Id, ScimGroupEntity.GroupRowKey, cancellationToken: ct);

            var oldExternalId = existing.Value.ExternalId;
            var oldOrgId = existing.Value.OrganizationId;

            var entity = ScimGroupEntity.FromModel(group);
            await scimGroupsTable.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);

            // Update external ID index if changed
            if (!string.IsNullOrEmpty(oldExternalId) && !string.IsNullOrEmpty(oldOrgId))
            {
                if (!string.Equals(oldExternalId, group.ExternalId, StringComparison.Ordinal) ||
                    !string.Equals(oldOrgId, group.OrganizationId, StringComparison.Ordinal))
                {
                    var oldIndexPk = $"{oldOrgId}|{oldExternalId}";
                    try
                    {
                        await scimGroupExternalIdsTable.DeleteEntityAsync(
                            oldIndexPk, ScimGroupEntity.GroupLookupRowKey, cancellationToken: ct);
                        if (tombstoneWriter is not null)
                            await tombstoneWriter.WriteAsync("ScimGroupExternalIds", oldIndexPk, ScimGroupEntity.GroupLookupRowKey, ct);
                    }
                    catch (RequestFailedException ex) when (ex.Status == 404) { }
                }
            }

            if (!string.IsNullOrEmpty(group.ExternalId) && !string.IsNullOrEmpty(group.OrganizationId))
            {
                var indexEntity = ScimGroupEntity.CreateExternalIdIndex(group.OrganizationId, group.ExternalId, group.Id);
                await scimGroupExternalIdsTable.UpsertEntityAsync(indexEntity, TableUpdateMode.Replace, ct);
            }
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            await CreateAsync(group, ct);
        }
    }

    public async Task DeleteAsync(string groupId, CancellationToken ct = default)
    {
        try
        {
            var existing = await scimGroupsTable.GetEntityAsync<ScimGroupEntity>(
                groupId, ScimGroupEntity.GroupRowKey, cancellationToken: ct);

            var group = existing.Value.ToModel();

            // Remove external ID index
            if (!string.IsNullOrEmpty(group.ExternalId) && !string.IsNullOrEmpty(group.OrganizationId))
            {
                var indexPk = $"{group.OrganizationId}|{group.ExternalId}";
                try
                {
                    await scimGroupExternalIdsTable.DeleteEntityAsync(
                        indexPk, ScimGroupEntity.GroupLookupRowKey, cancellationToken: ct);
                    if (tombstoneWriter is not null)
                        await tombstoneWriter.WriteAsync("ScimGroupExternalIds", indexPk, ScimGroupEntity.GroupLookupRowKey, ct);
                }
                catch (RequestFailedException ex) when (ex.Status == 404) { }
            }

            // Delete the group
            await scimGroupsTable.DeleteEntityAsync(groupId, ScimGroupEntity.GroupRowKey, cancellationToken: ct);
            if (tombstoneWriter is not null)
                await tombstoneWriter.WriteAsync("ScimGroups", groupId, ScimGroupEntity.GroupRowKey, ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
        }
    }
}
