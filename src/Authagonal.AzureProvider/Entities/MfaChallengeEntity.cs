using Azure;
using Azure.Data.Tables;
using Authagonal.Core.Models;
using Authagonal.Core.Services;

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

    /// <remarks>
    /// Takes the partitioner because <c>PartitionKey</c> carries the <c>{env}|</c> prefix outside the live
    /// env, and this field is the model's IDENTITY — it is echoed to callers and fed straight back into
    /// <c>PK()</c> on the next write. Unstripped, a read-modify-write re-prefixes it and targets a phantom
    /// row: the update returns 200 and changes nothing. Stripping was done at two of the nine stores that
    /// needed it, so it is a required parameter here rather than a call-site convention.
    /// </remarks>
    public MfaChallenge ToModel(EnvPartitioner partitioner) => new()
    {
        ChallengeId = partitioner.Strip(PartitionKey),
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
