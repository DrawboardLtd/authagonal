using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using Authagonal.Core.Models;

namespace Authagonal.Storage.Entities;

public sealed class OidcProviderEntity : ITableEntity
{
    public required string PartitionKey { get; set; }
    public required string RowKey { get; set; }
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public const string ConfigRowKey = "config";

    public required string ConnectionName { get; set; }
    public required string MetadataLocation { get; set; }
    public required string ClientId { get; set; }
    public required string ClientSecret { get; set; }
    public required string RedirectUrl { get; set; }
    public required string AllowedDomainsJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }

    public static OidcProviderEntity FromModel(OidcProviderConfig config) => new()
    {
        PartitionKey = config.ConnectionId,
        RowKey = ConfigRowKey,
        ConnectionName = config.ConnectionName,
        MetadataLocation = config.MetadataLocation,
        ClientId = config.ClientId,
        ClientSecret = config.ClientSecret,
        RedirectUrl = config.RedirectUrl,
        AllowedDomainsJson = JsonSerializer.Serialize(config.AllowedDomains),
        CreatedAt = config.CreatedAt,
        UpdatedAt = config.UpdatedAt,
    };

    public OidcProviderConfig ToModel() => new()
    {
        ConnectionId = PartitionKey,
        ConnectionName = ConnectionName,
        MetadataLocation = MetadataLocation,
        ClientId = ClientId,
        ClientSecret = ClientSecret,
        RedirectUrl = RedirectUrl,
        AllowedDomains = JsonSerializer.Deserialize<List<string>>(AllowedDomainsJson) ?? [],
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
    };
}
