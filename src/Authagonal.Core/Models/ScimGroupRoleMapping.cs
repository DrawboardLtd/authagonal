namespace Authagonal.Core.Models;

/// <summary>
/// Grants a role to the members of a SCIM/IdP group. Resolved at token issuance, so a
/// user's effective roles are their directly-assigned roles plus the roles of every
/// group they belong to. One row per (group, role) — a group may grant several roles.
/// </summary>
public sealed class ScimGroupRoleMapping
{
    /// <summary>The SCIM group's stable ID (matches <see cref="ScimGroup.Id"/>).</summary>
    public required string GroupId { get; set; }

    /// <summary>Denormalized group name, kept for display in management UIs.</summary>
    public string? GroupDisplayName { get; set; }

    /// <summary>The role granted to members of the group (e.g. "tenant:admin", "platform:sre").</summary>
    public required string Role { get; set; }
}
