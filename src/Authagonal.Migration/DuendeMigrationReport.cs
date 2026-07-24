namespace Authagonal.Migration;

/// <summary>
/// Result of an engine run — a superset of the old CLI's <c>MigrationStats</c> with per-pass
/// created/skipped counts plus validation findings. JSON-serializable (persisted into the run
/// marker's <c>StatsJson</c> and returned by the status endpoint).
/// </summary>
public sealed class DuendeMigrationReport
{
    public bool DryRun { get; set; }

    // Users
    public int UsersCreated { get; set; }
    public int UsersUpdated { get; set; }
    public int UsersSkipped { get; set; }

    // External logins
    public int LoginsCreated { get; set; }
    public int LoginsSkipped { get; set; }

    // Roles
    public int RolesCreated { get; set; }
    public int RolesSkipped { get; set; }

    // Scopes
    public int ScopesCreated { get; set; }
    public int ScopesSkipped { get; set; }

    // Clients
    public int ClientsCreated { get; set; }
    public int ClientsSkipped { get; set; }

    // API resources (flattened onto clients + scopes)
    public int ApiResourcesFlattened { get; set; }
    public int ApiResourcesSkipped { get; set; }

    // Federation
    public int SamlProvidersCreated { get; set; }
    public int OidcProvidersCreated { get; set; }
    public int SsoDomainsCreated { get; set; }

    // MFA
    public int MfaCredentialsCreated { get; set; }
    public int MfaUsersSkipped { get; set; }

    // Refresh tokens
    public int RefreshTokensCreated { get; set; }
    public int RefreshTokensSkipped { get; set; }

    // Validation findings
    public List<string> TablesFound { get; set; } = [];
    public List<string> InvalidUserIds { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public List<string> Errors { get; set; } = [];
}
