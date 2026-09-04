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
    /// The organization every user provisioned through this credential belongs to. Null or empty leaves
    /// users untagged, which is the previous behaviour and stays the default.
    /// </summary>
    /// <remarks>
    /// A connector knows which customer it is syncing; the SCIM protocol has no way to say so. Core SCIM
    /// has no organization attribute at all, and the enterprise extension is not implemented here, so
    /// without this the only way to tell one customer's synced users from another's was to give each
    /// customer its own OAuth client. That is a poor answer: it multiplies client registrations for what
    /// is really a property of the credential, and it conflates "who may write these users" with "which
    /// customer are they".
    /// <para>
    /// Binding it to the TOKEN rather than the client is deliberate, and mirrors
    /// <see cref="AllowedEmailDomains"/>: one client can hand out a credential per customer, and each
    /// credential says who its users are. Note this does NOT make the token an isolation boundary. Every
    /// SCIM ownership check keys on the client id, so two tokens on one client still see each other's
    /// users; this decides how those users are TAGGED, not who may touch them.
    /// </para>
    /// <para>
    /// Applied at creation only, and never on update: re-tagging an existing account is an administrative
    /// act, not something a routine sync should do silently. It also loses to nothing, because a
    /// provisioning app's <c>/try</c> response only assigns an organization when the user has none, so an
    /// explicit token binding wins over a downstream guess.
    /// </para>
    /// </remarks>
    public string? OrganizationId { get; set; }

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
