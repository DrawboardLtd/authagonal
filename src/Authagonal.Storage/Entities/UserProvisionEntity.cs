using Azure;
using Azure.Data.Tables;
using Authagonal.Core.Models;

namespace Authagonal.Storage.Entities;

public sealed class UserProvisionEntity : ITableEntity
{
    public required string PartitionKey { get; set; } // userId
    public required string RowKey { get; set; }       // appId
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public DateTimeOffset ProvisionedAt { get; set; }

    public static UserProvisionEntity FromModel(UserProvision model) => new()
    {
        PartitionKey = model.UserId,
        RowKey = model.AppId,
        ProvisionedAt = model.ProvisionedAt
    };

    public UserProvision ToModel() => new()
    {
        UserId = PartitionKey,
        AppId = RowKey,
        ProvisionedAt = ProvisionedAt
    };
}
