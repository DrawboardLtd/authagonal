using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using Authagonal.Core.Models;

namespace Authagonal.Storage.Entities;

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
        ShowInDiscoveryDocument = scope.ShowInDiscoveryDocument,
        UserClaimsJson = JsonSerializer.Serialize(scope.UserClaims, StorageJsonContext.Default.ListString),
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
        ShowInDiscoveryDocument = ShowInDiscoveryDocument,
        UserClaims = JsonSerializer.Deserialize(UserClaimsJson, StorageJsonContext.Default.ListString) ?? [],
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
    };
}
