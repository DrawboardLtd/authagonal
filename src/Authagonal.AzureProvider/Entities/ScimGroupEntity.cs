using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using Authagonal.Core.Models;
using Authagonal.Core.Services;

namespace Authagonal.AzureProvider.Entities;

public sealed class ScimGroupEntity : ITableEntity
{
    public required string PartitionKey { get; set; }
    public required string RowKey { get; set; }
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public const string GroupRowKey = "group";
    public const string GroupLookupRowKey = "group-lookup";

    public required string DisplayName { get; set; }
    public string? ExternalId { get; set; }
    public string? OrganizationId { get; set; }
    public string? MemberUserIdsJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }

    public static ScimGroupEntity FromModel(ScimGroup group) => new()
    {
        PartitionKey = group.Id,
        RowKey = GroupRowKey,
        DisplayName = group.DisplayName,
        ExternalId = group.ExternalId,
        OrganizationId = group.OrganizationId,
        MemberUserIdsJson = JsonSerializer.Serialize(group.MemberUserIds, AzureJsonContext.Default.ListString),
        CreatedAt = group.CreatedAt,
        UpdatedAt = group.UpdatedAt,
    };

    /// <remarks>
    /// Takes the partitioner because <c>PartitionKey</c> carries the <c>{env}|</c> prefix outside the live
    /// env, and this field is the model's IDENTITY — it is echoed to callers and fed straight back into
    /// <c>PK()</c> on the next write. Unstripped, a read-modify-write re-prefixes it and targets a phantom
    /// row: the update returns 200 and changes nothing. Stripping was done at two of the nine stores that
    /// needed it, so it is a required parameter here rather than a call-site convention.
    /// </remarks>
    public ScimGroup ToModel(EnvPartitioner partitioner) => new()
    {
        Id = partitioner.Strip(PartitionKey),
        DisplayName = DisplayName,
        ExternalId = ExternalId,
        OrganizationId = OrganizationId,
        MemberUserIds = string.IsNullOrEmpty(MemberUserIdsJson)
            ? []
            : JsonSerializer.Deserialize(MemberUserIdsJson, AzureJsonContext.Default.ListString) ?? [],
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
    };

    /// <summary>ExternalId index entity: PK="{orgId}|{externalId}", RK="group-lookup".</summary>
    public static ScimGroupExternalIdEntity CreateExternalIdIndex(string organizationId, string externalId, string groupId) => new()
    {
        PartitionKey = $"{organizationId}|{externalId}",
        RowKey = GroupLookupRowKey,
        GroupId = groupId,
    };
}

public sealed class ScimGroupExternalIdEntity : ITableEntity
{
    public required string PartitionKey { get; set; }
    public required string RowKey { get; set; }
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public required string GroupId { get; set; }
}
