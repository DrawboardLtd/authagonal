using Azure;
using Azure.Data.Tables;
using Authagonal.Core.Models;

namespace Authagonal.Storage.Entities;

public sealed class RoleEntity : ITableEntity
{
    public required string PartitionKey { get; set; }
    public required string RowKey { get; set; }
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public const string RolePartition = "role";

    public required string Name { get; set; }
    public string? Description { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }

    public static RoleEntity FromModel(Role role) => new()
    {
        PartitionKey = RolePartition,
        RowKey = role.Id,
        Name = role.Name,
        Description = role.Description,
        CreatedAt = role.CreatedAt,
        UpdatedAt = role.UpdatedAt,
    };

    public Role ToModel() => new()
    {
        Id = RowKey,
        Name = Name,
        Description = Description,
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
    };
}
