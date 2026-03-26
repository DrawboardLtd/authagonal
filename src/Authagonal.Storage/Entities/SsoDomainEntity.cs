using Azure;
using Azure.Data.Tables;
using Authagonal.Core.Models;

namespace Authagonal.Storage.Entities;

public sealed class SsoDomainEntity : ITableEntity
{
    public required string PartitionKey { get; set; }
    public required string RowKey { get; set; }
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public const string MappingRowKey = "mapping";

    public required string Domain { get; set; }
    public required string ProviderType { get; set; }
    public required string ConnectionId { get; set; }
    public required string Scheme { get; set; }

    public static SsoDomainEntity FromModel(SsoDomain domain) => new()
    {
        PartitionKey = domain.Domain.ToLowerInvariant(),
        RowKey = MappingRowKey,
        Domain = domain.Domain,
        ProviderType = domain.ProviderType,
        ConnectionId = domain.ConnectionId,
        Scheme = domain.Scheme,
    };

    public SsoDomain ToModel() => new()
    {
        Domain = Domain,
        ProviderType = ProviderType,
        ConnectionId = ConnectionId,
        Scheme = Scheme,
    };
}
