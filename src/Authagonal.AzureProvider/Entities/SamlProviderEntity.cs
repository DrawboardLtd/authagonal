using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using Authagonal.Core.Models;

namespace Authagonal.AzureProvider.Entities;

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
    /// <summary>Condensed pasted IdP metadata XML (F49) — set when the IdP has no metadata URL.</summary>
    public string? MetadataXml { get; set; }
    /// <summary>Requested NameIDPolicy format (F51): null = emailAddress default, "none" = omit.</summary>
    public string? NameIdFormat { get; set; }
    public required string AllowedDomainsJson { get; set; }
    public string? IconUrl { get; set; }
    /// <summary>
    /// Mirrors <see cref="SamlProviderConfig.DisableJitProvisioning"/>. Nullable
    /// for back-compat with rows written before this column existed; ToModel()
    /// coerces null → false to preserve the prior behaviour (JIT enabled).
    /// </summary>
    public bool? DisableJitProvisioning { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }

    public static SamlProviderEntity FromModel(SamlProviderConfig config) => new()
    {
        PartitionKey = config.ConnectionId,
        RowKey = ConfigRowKey,
        ConnectionName = config.ConnectionName,
        EntityId = config.EntityId,
        MetadataLocation = config.MetadataLocation,
        MetadataXml = config.MetadataXml,
        NameIdFormat = config.NameIdFormat,
        AllowedDomainsJson = JsonSerializer.Serialize(config.AllowedDomains, AzureJsonContext.Default.ListString),
        IconUrl = config.IconUrl,
        DisableJitProvisioning = config.DisableJitProvisioning,
        CreatedAt = config.CreatedAt,
        UpdatedAt = config.UpdatedAt,
    };

    public SamlProviderConfig ToModel() => new()
    {
        ConnectionId = PartitionKey,
        ConnectionName = ConnectionName,
        EntityId = EntityId,
        MetadataLocation = MetadataLocation,
        MetadataXml = MetadataXml,
        NameIdFormat = NameIdFormat,
        AllowedDomains = JsonSerializer.Deserialize(AllowedDomainsJson, AzureJsonContext.Default.ListString) ?? [],
        IconUrl = IconUrl,
        DisableJitProvisioning = DisableJitProvisioning ?? false,
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
    };
}
