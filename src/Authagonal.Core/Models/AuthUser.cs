namespace Authagonal.Core.Models;

public sealed class AuthUser
{
    public required string Id { get; set; }
    public required string Email { get; set; }
    public required string NormalizedEmail { get; set; }
    public string? PasswordHash { get; set; }

    /// <summary>
    /// A password STAGED by a passwordless-account claim (AllowPasswordlessAccountClaim), inert
    /// until the claim's fresh email confirmation promotes it to PasswordHash. Staging — instead of
    /// setting PasswordHash gated on EmailConfirmed — keeps the account genuinely passwordless in
    /// the meantime: still claimable (latest claim wins; the inbox owner arbitrates by clicking),
    /// federation login untouched, and an attacker's unconfirmed claim can never block the real
    /// owner's later upgrade.
    /// </summary>
    public string? PendingPasswordHash { get; set; }

    /// <summary>
    /// Profile/attribute changes STAGED by a passwordless-account claim, held as JSON until the claim's
    /// fresh email confirmation applies them (alongside promoting <see cref="PendingPasswordHash"/>).
    /// Staging keeps a claim from mutating the victim account's name/custom-attributes — which ride the
    /// real owner's tokens — before inbox ownership is proven.
    /// </summary>
    public string? PendingClaimJson { get; set; }
    public bool EmailConfirmed { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? CompanyName { get; set; }
    public string? Phone { get; set; }
    /// <summary>Preferred UI/communication language as a BCP-47 tag (e.g. "de", "zh-Hans"). Captured
    /// from the request-time UI language at registration; null means no preference (fall back to English).</summary>
    public string? Locale { get; set; }
    public string? OrganizationId { get; set; }
    public int AccessFailedCount { get; set; }
    public bool LockoutEnabled { get; set; }
    public DateTimeOffset? LockoutEnd { get; set; }
    public string? SecurityStamp { get; set; }
    public bool MfaEnabled { get; set; }
    public string? ExternalId { get; set; }
    public bool IsActive { get; set; } = true;
    public string? ScimProvisionedByClientId { get; set; }

    /// <summary>
    /// When the provisioning client deleted this resource over SCIM. Non-null means the record is a
    /// tombstone: RFC 7644 §3.6 lets a provider keep the row, but then it MUST answer 404 for every
    /// operation on it and MUST omit it from query results.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="IsActive"/>, which is the directory's `active` flag and describes a
    /// user who still exists. Deactivation is reversible by the provisioning client; a tombstone is
    /// only reclaimed by creating the resource afresh.
    /// </remarks>
    public DateTimeOffset? ScimDeletedAt { get; set; }
    public List<string> Roles { get; set; } = [];
    public Dictionary<string, string> CustomAttributes { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
}
