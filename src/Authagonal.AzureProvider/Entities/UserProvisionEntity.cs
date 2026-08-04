using Azure;
using Azure.Data.Tables;
using Authagonal.Core.Models;
using Authagonal.Core.Services;

namespace Authagonal.AzureProvider.Entities;

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

    /// <remarks>
    /// Takes the partitioner because <c>PartitionKey</c> carries the <c>{env}|</c> prefix outside the live
    /// env, and this field is the model's IDENTITY — it is echoed to callers and fed straight back into
    /// <c>PK()</c> on the next write. Unstripped, a read-modify-write re-prefixes it and targets a phantom
    /// row: the update returns 200 and changes nothing. Stripping was done at two of the nine stores that
    /// needed it, so it is a required parameter here rather than a call-site convention.
    /// </remarks>
    public UserProvision ToModel(EnvPartitioner partitioner) => new()
    {
        UserId = partitioner.Strip(PartitionKey),
        AppId = RowKey,
        ProvisionedAt = ProvisionedAt
    };
}
