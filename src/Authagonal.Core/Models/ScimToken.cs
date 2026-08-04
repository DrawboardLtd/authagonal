namespace Authagonal.Core.Models;

public sealed class ScimToken
{
    public required string TokenId { get; set; }
    public required string ClientId { get; set; }
    public required string TokenHash { get; set; }
    public string? Description { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public bool IsRevoked { get; set; }

    /// <summary>
    /// Email domains this credential may provision into. Empty means unrestricted.
    /// </summary>
    /// <remarks>
    /// The only control bounding WHICH identities a provisioning connector may mint, and it was reachable
    /// solely from <c>Scim:Clients:{clientId}:AllowedEmailDomains</c> — a configuration key no document
    /// mentioned and which the documented token-minting flow (<c>POST /api/v1/scim/tokens</c>) could not
    /// write. So the shipped path produced an unrestricted credential and there was no way to narrow it
    /// without editing configuration by hand.
    /// <para>
    /// That matters because a SCIM-created user is written with <c>EmailConfirmed = true</c>: an unrestricted
    /// connector could mint <c>ceo@someone-elses-domain.example</c> as a pre-verified account, and
    /// <c>FederationAdoptionPolicy</c> adopts a record with no external logins unconditionally — so the real
    /// owner's first federated sign-in binds to the squatted row, which
    /// <c>ScimProvisionedByClientId</c> leaves owned by the squatting connector permanently.
    /// </para>
    /// Intersected with the configuration key rather than replacing it, so minting a token can only ever
    /// narrow an operator's existing bound, never widen it.
    /// </remarks>
    public List<string> AllowedEmailDomains { get; set; } = [];
}
