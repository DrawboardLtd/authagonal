using Azure;
using Azure.Data.Tables;
using Authagonal.Core.Models;

namespace Authagonal.Storage.Entities;

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
    };
}
