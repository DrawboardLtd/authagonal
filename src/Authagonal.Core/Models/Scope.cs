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
    public List<string> UserClaims { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}
