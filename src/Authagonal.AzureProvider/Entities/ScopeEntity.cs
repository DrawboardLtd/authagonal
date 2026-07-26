using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using Authagonal.Core.Models;

namespace Authagonal.AzureProvider.Entities;

public sealed class ScopeEntity : ITableEntity
{
    public required string PartitionKey { get; set; }
    public required string RowKey { get; set; }
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public const string ScopePartition = "scope";

    public string? DisplayName { get; set; }
    public string? Description { get; set; }
    public bool Emphasize { get; set; }
    public bool Required { get; set; }

    /// <summary>
    /// The consent-screen heading this scope is filed under.
    /// </summary>
    /// <remarks>
    /// Table entities enumerate their columns, so a field added to <c>Scope</c> and to the seeders still
    /// vanishes unless it is added HERE too: the write drops it and the read never looks for it. That is
    /// exactly how Group shipped broken — set in memory, seeded on every boot, and null by the time
    /// anything asked for it. The Dynamo store serialises the whole model and so never had the problem.
    /// </remarks>
    public string? Group { get; set; }
    public bool ShowInDiscoveryDocument { get; set; } = true;
    public string UserClaimsJson { get; set; } = "[]";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }

    public static ScopeEntity FromModel(Scope scope) => new()
    {
        PartitionKey = ScopePartition,
        RowKey = scope.Name,
        DisplayName = scope.DisplayName,
        Description = scope.Description,
        Emphasize = scope.Emphasize,
        Required = scope.Required,
        Group = scope.Group,
        ShowInDiscoveryDocument = scope.ShowInDiscoveryDocument,
        UserClaimsJson = JsonSerializer.Serialize(scope.UserClaims, AzureJsonContext.Default.ListString),
        CreatedAt = scope.CreatedAt,
        UpdatedAt = scope.UpdatedAt,
    };

    public Scope ToModel() => new()
    {
        Name = RowKey,
        DisplayName = DisplayName,
        Description = Description,
        Emphasize = Emphasize,
        Required = Required,
        Group = Group,
        ShowInDiscoveryDocument = ShowInDiscoveryDocument,
        UserClaims = JsonSerializer.Deserialize(UserClaimsJson, AzureJsonContext.Default.ListString) ?? [],
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
    };
}
