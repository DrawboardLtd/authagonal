using Azure;
using Azure.Data.Tables;
using Authagonal.Core.Authority;
using Authagonal.Core.Models;

namespace Authagonal.AzureProvider.Entities;

public sealed class AgentProfileEntity : ITableEntity
{
    public required string PartitionKey { get; set; }
    public required string RowKey { get; set; }
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public const string AgentsPartition = "agent";

    public string Mode { get; set; } = "delegated";

    /// <summary>The ceiling in its RFC 9396 wire form (JSON array).</summary>
    public string CeilingJson { get; set; } = "[]";

    public int MaxDelegationDepth { get; set; }
    public int MaxTokenLifetimeSeconds { get; set; } = 300;
    public string HighRiskDefault { get; set; } = "ask";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }

    public static AgentProfileEntity FromModel(AgentProfile profile) => new()
    {
        PartitionKey = AgentsPartition,
        RowKey = profile.ClientId,
        Mode = AgentModes.Name(profile.Mode),
        CeilingJson = AuthorityJson.Serialize(profile.Ceiling),
        MaxDelegationDepth = profile.MaxDelegationDepth,
        MaxTokenLifetimeSeconds = profile.MaxTokenLifetimeSeconds,
        HighRiskDefault = AuthorityJson.PolicyName(profile.HighRiskDefault),
        CreatedAt = profile.CreatedAt,
        UpdatedAt = profile.UpdatedAt,
    };

    public AgentProfile ToModel() => new()
    {
        ClientId = RowKey,
        Mode = AgentModes.Parse(Mode),
        // A garbled ceiling reads as Empty (grants nothing) — a stored ceiling must never
        // widen because a row was corrupted.
        Ceiling = AuthorityJson.TryParse(CeilingJson, out var ceiling) ? ceiling : AuthoritySet.Empty,
        MaxDelegationDepth = MaxDelegationDepth,
        MaxTokenLifetimeSeconds = MaxTokenLifetimeSeconds,
        HighRiskDefault = AuthorityJson.ParsePolicyName(HighRiskDefault),
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
    };
}
