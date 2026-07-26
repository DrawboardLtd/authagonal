namespace Authagonal.Core.Models;

public sealed class Scope
{
    public required string Name { get; set; }
    public string? DisplayName { get; set; }
    public string? Description { get; set; }
    public bool Emphasize { get; set; }

    /// <summary>
    /// A heading to file this scope under on the consent screen, or null to show it on its own.
    /// </summary>
    /// <remarks>
    /// Presentation only — it never affects what is granted. It exists because a client asking for
    /// fifteen scopes produces a list nobody reads to the end of, and the decision a person is actually
    /// making ("may it touch my drawings at all?") is the group, not the individual read and write
    /// beneath it.
    /// </remarks>
    public string? Group { get; set; }
    public bool Required { get; set; }
    public bool ShowInDiscoveryDocument { get; set; } = true;

    /// <summary>
    /// Roles a user must hold to be granted this scope. Empty — the default — leaves the scope
    /// ungated, which is every scope until an operator says otherwise.
    /// </summary>
    /// <remarks>
    /// This is a gate on the SUBJECT, not on the client: a client's <c>AllowedScopes</c> already says
    /// what it may ask for, and that check happens before anyone has logged in. This one runs once the
    /// user is known and silently drops the scopes they are not entitled to, so a client can ask for
    /// its full set and each user gets back the subset they qualify for. Standard OAuth downscoping —
    /// the token response echoes the granted <c>scope</c>, so the client is told it got less.
    /// <para>
    /// Dropping rather than failing is deliberate: an application whose staff surface is one scope
    /// among several must still be usable by everyone else. Only a request where EVERY scope is
    /// dropped fails, because there is nothing left to issue a token for.
    /// </para>
    /// </remarks>
    public List<string> AllowedRoles { get; set; } = [];

    public List<string> UserClaims { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}
