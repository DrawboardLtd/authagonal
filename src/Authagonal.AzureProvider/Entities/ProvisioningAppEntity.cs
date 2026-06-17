using Azure;
using Azure.Data.Tables;
using Authagonal.Core.Models;

namespace Authagonal.AzureProvider.Entities;

public sealed class ProvisioningAppEntity : ITableEntity
{
    public required string PartitionKey { get; set; }
    public required string RowKey { get; set; }
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public const string AppsPartition = "app";

    public required string Name { get; set; }
    public required string CallbackUrl { get; set; }
    public string? ApiKey { get; set; }
    public int? TryTimeoutSeconds { get; set; }

    public static ProvisioningAppEntity FromModel(ProvisioningAppConfig app) => new()
    {
        PartitionKey = AppsPartition,
        RowKey = app.AppId,
        Name = app.Name,
        CallbackUrl = app.CallbackUrl,
        ApiKey = app.ApiKey,
        TryTimeoutSeconds = app.TryTimeoutSeconds,
    };

    public ProvisioningAppConfig ToModel() => new()
    {
        AppId = RowKey,
        Name = Name,
        CallbackUrl = CallbackUrl,
        ApiKey = ApiKey,
        TryTimeoutSeconds = TryTimeoutSeconds,
    };
}
