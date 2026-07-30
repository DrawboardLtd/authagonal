using Azure;
using Azure.Data.Tables;
using Authagonal.Core.Models;

namespace Authagonal.AzureProvider.Entities;

public sealed class MfaChallengeEntity : ITableEntity
{
    public required string PartitionKey { get; set; } // ChallengeId
    public required string RowKey { get; set; }        // "challenge"
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public const string ChallengeRowKey = "challenge";

    public required string UserId { get; set; }
    public string? ClientId { get; set; }
    public string? ReturnUrl { get; set; }
    public string? WebAuthnChallenge { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public bool IsConsumed { get; set; }
    public int Attempts { get; set; }

    /// <summary>Stored as the underlying int. A row written before this column existed reads back as 0,
    /// which is <see cref="MfaChallengePurpose.Verify"/> — the least-privileged case.</summary>
    public int Purpose { get; set; }

    public static MfaChallengeEntity FromModel(MfaChallenge challenge) => new()
    {
        PartitionKey = challenge.ChallengeId,
        RowKey = ChallengeRowKey,
        UserId = challenge.UserId,
        ClientId = challenge.ClientId,
        ReturnUrl = challenge.ReturnUrl,
        WebAuthnChallenge = challenge.WebAuthnChallenge,
        CreatedAt = challenge.CreatedAt,
        ExpiresAt = challenge.ExpiresAt,
        IsConsumed = challenge.IsConsumed,
        Attempts = challenge.Attempts,
        Purpose = (int)challenge.Purpose,
    };

    public MfaChallenge ToModel() => new()
    {
        ChallengeId = PartitionKey,
        UserId = UserId,
        ClientId = ClientId,
        ReturnUrl = ReturnUrl,
        WebAuthnChallenge = WebAuthnChallenge,
        CreatedAt = CreatedAt,
        ExpiresAt = ExpiresAt,
        IsConsumed = IsConsumed,
        Attempts = Attempts,
        Purpose = (MfaChallengePurpose)Purpose,
    };
}
