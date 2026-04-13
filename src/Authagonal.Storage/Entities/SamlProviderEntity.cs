using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using Authagonal.Core.Models;

namespace Authagonal.Storage.Entities;

public sealed class SamlProviderEntity : ITableEntity
{
    public required string PartitionKey { get; set; }
    public required string RowKey { get; set; }
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public const string ConfigRowKey = "config";

    public required string ConnectionName { get; set; }
    public required string EntityId { get; set; }
    public required string MetadataLocation { get; set; }
    public required string AllowedDomainsJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }

    public static SamlProviderEntity FromModel(SamlProviderConfig config) => new()
    {
        PartitionKey = config.ConnectionId,
        RowKey = ConfigRowKey,
        ConnectionName = config.ConnectionName,
        EntityId = config.EntityId,
        MetadataLocation = config.MetadataLocation,
        AllowedDomainsJson = JsonSerializer.Serialize(config.AllowedDomains, StorageJsonContext.Default.ListString),
        CreatedAt = config.CreatedAt,
        UpdatedAt = config.UpdatedAt,
    };

    public SamlProviderConfig ToModel() => new()
    {
        ConnectionId = PartitionKey,
        ConnectionName = ConnectionName,
        EntityId = EntityId,
        MetadataLocation = MetadataLocation,
        AllowedDomains = JsonSerializer.Deserialize(AllowedDomainsJson, StorageJsonContext.Default.ListString) ?? [],
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
    };
}
