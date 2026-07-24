using System.Collections.Concurrent;
using System.Security.Cryptography;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Server.Services;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Authagonal.Migration;

/// <summary>
/// Copies a Duende IdentityServer SQL database into Authagonal's stores, one pass per entity kind.
/// Every pass is wrapped so a failure is reported and skipped rather than aborting the copy, and
/// every store write is gated on <see cref="DuendeMigrationOptions.DryRun"/> — a dry run walks the
/// whole source and produces the full report without writing anything.
///
/// Product-agnostic by design: writes stores directly, so host provisioning callbacks never fire
/// for migrated users. Ids are preserved verbatim (a Duende <c>sub</c> is the Authagonal user id).
/// </summary>
public sealed class DuendeMigrationEngine(
    DuendeMigrationStores stores,
    ISecretProvider secretProvider,
    RecoveryCodeService recoveryCodes,
    ILogger<DuendeMigrationEngine>? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger<DuendeMigrationEngine>.Instance;

    // Entities this run created — so the ApiResources flatten only mutates migration-created
    // clients/scopes and leaves config-seeded ones (which win) untouched.
    private readonly HashSet<string> _createdClientIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _createdScopeNames = new(StringComparer.OrdinalIgnoreCase);

    public async Task<DuendeMigrationReport> RunAsync(DuendeMigrationOptions options, CancellationToken ct = default)
    {
        _createdClientIds.Clear();
        _createdScopeNames.Clear();

        var connectionString = options.Source.ConnectionString
            ?? throw new InvalidOperationException("Migration:Source:ConnectionString is required.");

        var report = new DuendeMigrationReport { DryRun = options.DryRun };
        var now = DateTimeOffset.UtcNow;

        await using var sql = new SqlConnection(connectionString);
        await sql.OpenAsync(ct);

        // 1. Validate — inventory + findings. The whole of a dry run's value.
        await RunPass(report, "Validate", () => ValidateAsync(sql, report, ct));

        // 2. Scopes (ApiScopes + IdentityResources).
        await RunPass(report, "Scopes", async () =>
        {
            await MigrateScopesAsync(sql, "ApiScopes", options, report, now, ct);
            await MigrateScopesAsync(sql, "IdentityResources", options, report, now, ct);
        });

        // 3. Clients.
        if (options.MigrateClients)
            await RunPass(report, "Clients", () => MigrateClientsAsync(sql, options, report, ct));

        // 4. ApiResources flatten (audiences → clients, claims → scopes).
        await RunPass(report, "ApiResources", () => FlattenApiResourcesAsync(sql, options, report, ct));

        // 5. Roles (+ id→name map for the users pass).
        var roleIdToName = new Dictionary<string, string>(StringComparer.Ordinal);
        await RunPass(report, "Roles", () => MigrateRolesAsync(sql, options, roleIdToName, report, now, ct));

        // 6. Users.
        await RunPass(report, "Users", () => MigrateUsersAsync(sql, options, roleIdToName, report, now, ct));

        // 7. External logins.
        await RunPass(report, "ExternalLogins", () => MigrateExternalLoginsAsync(sql, options, report, ct));

        // 8. MFA (TOTP + recovery codes).
        await RunPass(report, "Mfa", () => MigrateMfaAsync(sql, options, report, now, ct));

        // 9. SAML/OIDC providers + SSO domains.
        await RunPass(report, "Providers", () => MigrateProvidersAsync(sql, options, report, ct));

        // 10. Refresh tokens (opt-in, default off).
        if (options.MigrateRefreshTokens)
            await RunPass(report, "RefreshTokens", () => MigrateRefreshTokensAsync(sql, options, report, ct));

        return report;
    }

    private async Task RunPass(DuendeMigrationReport report, string name, Func<Task> body)
    {
        try
        {
            _logger.LogInformation("Duende migration pass '{Pass}' starting", name);
            await body();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Duende migration pass '{Pass}' failed", name);
            report.Errors.Add($"{name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Writes an independent set of entities with bounded concurrency. The high-volume passes read
    /// their rows sequentially (one forward-only SQL reader) then fan the writes out through here —
    /// entities have no referential integrity, so the only bound is Azure Table throughput.
    /// </summary>
    private Task ForEachAsync<T>(IReadOnlyList<T> items, DuendeMigrationOptions options, Func<T, Task> body, CancellationToken ct)
    {
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, options.MaxDegreeOfParallelism),
            CancellationToken = ct,
        };
        return Parallel.ForEachAsync(items, parallelOptions, async (item, _) => await body(item));
    }

    // ---------------------------------------------------------------------------
    // 1. Validate
    // ---------------------------------------------------------------------------
    private static async Task ValidateAsync(SqlConnection sql, DuendeMigrationReport report, CancellationToken ct)
    {
        await using (var cmd = sql.CreateCommand())
        {
            cmd.CommandText = "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'BASE TABLE' ORDER BY TABLE_NAME";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                report.TablesFound.Add(reader.GetString(0));
        }

        if (!report.TablesFound.Contains("AspNetUsers") && !report.TablesFound.Contains("Clients"))
        {
            report.Warnings.Add("Neither 'AspNetUsers' nor 'Clients' found — does not look like a Duende IdentityServer database.");
            return;
        }

        if (await sql.TableExistsAsync("AspNetUsers", ct))
        {
            // Duplicate / null emails (prod has neither; a drift would break the users pass).
            await using (var cmd = sql.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT LOWER(Email) AS e, COUNT(*) AS c
                    FROM AspNetUsers WHERE Email IS NOT NULL
                    GROUP BY LOWER(Email) HAVING COUNT(*) > 1
                    """;
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    report.Warnings.Add($"Duplicate email '{reader.GetString(0)}' × {reader.GetInt32(1)} — later rows may collide.");
            }

            await using (var cmd = sql.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM AspNetUsers WHERE Email IS NULL OR Email = ''";
                var nullEmails = (int)(await cmd.ExecuteScalarAsync(ct))!;
                if (nullEmails > 0)
                    report.Warnings.Add($"{nullEmails} user(s) with null/empty Email — will fall back to UserName.");
            }

            // 2FA-enabled users with no AuthenticatorKey token (correctness check; prod has zero MFA).
            if (await sql.TableExistsAsync("AspNetUserTokens", ct))
            {
                await using var cmd = sql.CreateCommand();
                cmd.CommandText = """
                    SELECT COUNT(*) FROM AspNetUsers u
                    WHERE u.TwoFactorEnabled = 1
                      AND NOT EXISTS (SELECT 1 FROM AspNetUserTokens t WHERE t.UserId = u.Id AND t.Name = 'AuthenticatorKey')
                    """;
                var count = (int)(await cmd.ExecuteScalarAsync(ct))!;
                if (count > 0)
                    report.Warnings.Add($"{count} user(s) have TwoFactorEnabled but no AuthenticatorKey — MFA cannot be migrated for them.");
            }
        }
    }

    // ---------------------------------------------------------------------------
    // 2. Scopes
    // ---------------------------------------------------------------------------
    private async Task MigrateScopesAsync(
        SqlConnection sql, string table, DuendeMigrationOptions options,
        DuendeMigrationReport report, DateTimeOffset now, CancellationToken ct)
    {
        if (!await sql.TableExistsAsync(table, ct))
            return;

        var existing = (await stores.Scopes.ListAsync(ct)).Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var scopes = new List<(int Id, Scope Scope, bool Enabled)>();
        await using (var cmd = sql.CreateCommand())
        {
            cmd.CommandText = $"SELECT Id, Name, DisplayName, Description, Required, Emphasize, ShowInDiscoveryDocument, Enabled FROM {table}";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var name = reader.GetString(1);
                if (existing.Contains(name)) { report.ScopesSkipped++; continue; }

                scopes.Add((
                    reader.GetInt32(0),
                    new Scope
                    {
                        Name = name,
                        DisplayName = reader.IsDBNull(2) ? null : reader.GetString(2),
                        Description = reader.IsDBNull(3) ? null : reader.GetString(3),
                        Required = !reader.IsDBNull(4) && reader.GetBoolean(4),
                        Emphasize = !reader.IsDBNull(5) && reader.GetBoolean(5),
                        ShowInDiscoveryDocument = reader.IsDBNull(6) || reader.GetBoolean(6),
                        CreatedAt = now,
                    },
                    reader.IsDBNull(7) || reader.GetBoolean(7)));
            }
        }

        // Scope claims.
        var claimsTable = table == "ApiScopes" ? "ApiScopeClaims" : "IdentityResourceClaims";
        var claimsFk = table == "ApiScopes" ? "ScopeId" : "IdentityResourceId";
        if (await sql.TableExistsAsync(claimsTable, ct))
        {
            var claimsById = new Dictionary<int, List<string>>();
            await using var cmd = sql.CreateCommand();
            cmd.CommandText = $"SELECT {claimsFk}, Type FROM {claimsTable}";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var sid = reader.GetInt32(0);
                if (!claimsById.TryGetValue(sid, out var list))
                    claimsById[sid] = list = [];
                if (!reader.IsDBNull(1)) list.Add(reader.GetString(1));
            }
            foreach (var entry in scopes)
                if (claimsById.TryGetValue(entry.Id, out var list))
                    entry.Scope.UserClaims = list;
        }

        foreach (var (_, scope, enabled) in scopes)
        {
            if (!enabled) { report.ScopesSkipped++; continue; }
            if (!options.DryRun) await stores.Scopes.CreateAsync(scope, ct);
            _createdScopeNames.Add(scope.Name);
            report.ScopesCreated++;
        }
    }

    // ---------------------------------------------------------------------------
    // 3. Clients
    // ---------------------------------------------------------------------------
    private async Task MigrateClientsAsync(
        SqlConnection sql, DuendeMigrationOptions options, DuendeMigrationReport report, CancellationToken ct)
    {
        if (!await sql.TableExistsAsync("Clients", ct))
            return;

        var clients = new Dictionary<int, OAuthClient>();
        await using (var cmd = sql.CreateCommand())
        {
            cmd.CommandText = """
                SELECT Id, ClientId, ClientName, Description, ClientUri, LogoUri, Enabled,
                       RequirePkce, RequireClientSecret,
                       AllowOfflineAccess, AlwaysIncludeUserClaimsInIdToken, RequireConsent,
                       AccessTokenLifetime, IdentityTokenLifetime, AuthorizationCodeLifetime,
                       AbsoluteRefreshTokenLifetime, SlidingRefreshTokenLifetime,
                       RefreshTokenUsage, RefreshTokenExpiration,
                       FrontChannelLogoutUri, FrontChannelLogoutSessionRequired,
                       BackChannelLogoutUri, BackChannelLogoutSessionRequired,
                       DeviceCodeLifetime
                FROM Clients
                """;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var pkId = reader.GetInt32(0);
                // Duende RefreshTokenExpiration: 0 = Sliding, 1 = Absolute (inverted from our enum).
                var duendeRte = reader.IsDBNull(18) ? 1 : reader.GetInt32(18);
                clients[pkId] = new OAuthClient
                {
                    ClientId = reader.GetString(1),
                    ClientName = reader.IsDBNull(2) ? "" : reader.GetString(2),
                    Description = reader.IsDBNull(3) ? null : reader.GetString(3),
                    ClientUri = reader.IsDBNull(4) ? null : reader.GetString(4),
                    LogoUri = reader.IsDBNull(5) ? null : reader.GetString(5),
                    Enabled = reader.IsDBNull(6) || reader.GetBoolean(6),
                    RequirePkce = !reader.IsDBNull(7) && reader.GetBoolean(7),
                    RequireClientSecret = reader.IsDBNull(8) || reader.GetBoolean(8),
                    AllowOfflineAccess = !reader.IsDBNull(9) && reader.GetBoolean(9),
                    AlwaysIncludeUserClaimsInIdToken = !reader.IsDBNull(10) && reader.GetBoolean(10),
                    RequireConsent = !reader.IsDBNull(11) && reader.GetBoolean(11),
                    AccessTokenLifetimeSeconds = reader.IsDBNull(12) ? 1800 : reader.GetInt32(12),
                    IdentityTokenLifetimeSeconds = reader.IsDBNull(13) ? 300 : reader.GetInt32(13),
                    AuthorizationCodeLifetimeSeconds = reader.IsDBNull(14) ? 300 : reader.GetInt32(14),
                    AbsoluteRefreshTokenLifetimeSeconds = reader.IsDBNull(15) ? 2592000 : reader.GetInt32(15),
                    SlidingRefreshTokenLifetimeSeconds = reader.IsDBNull(16) ? 1296000 : reader.GetInt32(16),
                    RefreshTokenUsage = (!reader.IsDBNull(17) && reader.GetInt32(17) == 1) ? RefreshTokenUsage.OneTime : RefreshTokenUsage.ReUse,
                    RefreshTokenExpiration = duendeRte == 0 ? RefreshTokenExpiration.Sliding : RefreshTokenExpiration.Absolute,
                    FrontChannelLogoutUri = reader.IsDBNull(19) ? null : reader.GetString(19),
                    FrontChannelLogoutSessionRequired = reader.IsDBNull(20) || reader.GetBoolean(20),
                    BackChannelLogoutUri = reader.IsDBNull(21) ? null : reader.GetString(21),
                    BackChannelLogoutSessionRequired = reader.IsDBNull(22) || reader.GetBoolean(22),
                    DeviceCodeLifetimeSeconds = reader.IsDBNull(23) ? 300 : reader.GetInt32(23),
                };
            }
        }

        await FillClientSecretsAsync(sql, clients, report, ct);
        await FillClientChildAsync(sql, "ClientGrantTypes", "GrantType", clients, (c, v) => c.AllowedGrantTypes.Add(v), ct);
        await FillClientChildAsync(sql, "ClientScopes", "Scope", clients, (c, v) => c.AllowedScopes.Add(v), ct);
        await FillClientChildAsync(sql, "ClientRedirectUris", "RedirectUri", clients, (c, v) => c.RedirectUris.Add(v), ct);
        await FillClientChildAsync(sql, "ClientPostLogoutRedirectUris", "PostLogoutRedirectUri", clients, (c, v) => c.PostLogoutRedirectUris.Add(v), ct);
        await FillClientChildAsync(sql, "ClientCorsOrigins", "Origin", clients, (c, v) => c.AllowedCorsOrigins.Add(v), ct);

        foreach (var client in clients.Values)
        {
            // Seed wins: a config-seeded client of the same id is left untouched.
            if (await stores.Clients.GetAsync(client.ClientId, ct) is not null)
            {
                report.ClientsSkipped++;
                continue;
            }

            if (!options.DryRun) await stores.Clients.UpsertAsync(client, ct);
            _createdClientIds.Add(client.ClientId);
            report.ClientsCreated++;
        }
    }

    private static async Task FillClientSecretsAsync(
        SqlConnection sql, Dictionary<int, OAuthClient> clients, DuendeMigrationReport report, CancellationToken ct)
    {
        if (!await sql.TableExistsAsync("ClientSecrets", ct))
            return;

        var now = DateTime.UtcNow;
        var expiredSkipped = 0;
        var untaggedSkipped = 0;

        await using var cmd = sql.CreateCommand();
        cmd.CommandText = "SELECT ClientId, Value, Expiration FROM ClientSecrets";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var cid = reader.GetInt32(0);
            if (reader.IsDBNull(1) || !clients.TryGetValue(cid, out var client)) continue;
            if (!reader.IsDBNull(2) && reader.GetDateTime(2) <= now) { expiredSkipped++; continue; }

            var tagged = DuendeMappings.TagClientSecret(reader.GetString(1));
            if (tagged is null) { untaggedSkipped++; continue; }
            client.ClientSecretHashes.Add(tagged);
        }

        if (expiredSkipped > 0)
            report.Warnings.Add($"{expiredSkipped} client secret(s) skipped — expired at source. Rotate post-migration.");
        if (untaggedSkipped > 0)
            report.Warnings.Add($"{untaggedSkipped} client secret(s) skipped — not a recognized SHA-256/512 digest length. Re-seed those secrets.");
    }

    private static async Task FillClientChildAsync(
        SqlConnection sql, string table, string column,
        Dictionary<int, OAuthClient> clients, Action<OAuthClient, string> add, CancellationToken ct)
    {
        if (!await sql.TableExistsAsync(table, ct))
            return;

        await using var cmd = sql.CreateCommand();
        cmd.CommandText = $"SELECT ClientId, {column} FROM {table}";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var cid = reader.GetInt32(0);
            if (reader.IsDBNull(1) || !clients.TryGetValue(cid, out var client)) continue;
            add(client, reader.GetString(1));
        }
    }

    // ---------------------------------------------------------------------------
    // 4. ApiResources flatten
    // ---------------------------------------------------------------------------
    private async Task FlattenApiResourcesAsync(
        SqlConnection sql, DuendeMigrationOptions options, DuendeMigrationReport report, CancellationToken ct)
    {
        if (!await sql.TableExistsAsync("ApiResources", ct))
            return;

        var resources = new Dictionary<int, (string Name, bool Enabled)>();
        await using (var cmd = sql.CreateCommand())
        {
            cmd.CommandText = "SELECT Id, Name, Enabled FROM ApiResources";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var name = reader.IsDBNull(1) ? null : reader.GetString(1);
                if (string.IsNullOrEmpty(name)) continue;
                resources[reader.GetInt32(0)] = (name, reader.IsDBNull(2) || reader.GetBoolean(2));
            }
        }
        if (resources.Count == 0) return;

        var claimsByResource = await LoadIntStringMapAsync(sql, "ApiResourceClaims", "ApiResourceId", "Type", ct);
        var scopesByResource = await LoadIntStringMapAsync(sql, "ApiResourceScopes", "ApiResourceId", "Scope", ct);

        var liveClients = await stores.Clients.GetAllAsync(ct);
        var liveScopes = await stores.Scopes.ListAsync(ct);
        var scopesByName = liveScopes.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);

        var dirtyClients = new HashSet<OAuthClient>();
        var dirtyScopes = new HashSet<Scope>();

        foreach (var (rid, (name, enabled)) in resources)
        {
            if (!enabled)
            {
                report.Warnings.Add($"ApiResource '{name}' skipped — disabled at source.");
                report.ApiResourcesSkipped++;
                continue;
            }

            var memberScopes = scopesByResource.TryGetValue(rid, out var names) ? names : [];

            // Claims → member scopes (only migration-created scopes; seeded scopes win).
            if (claimsByResource.TryGetValue(rid, out var claimTypes) && claimTypes.Count > 0)
            {
                foreach (var scopeName in memberScopes)
                {
                    if (!_createdScopeNames.Contains(scopeName)) continue;
                    if (!scopesByName.TryGetValue(scopeName, out var scope)) continue;
                    var before = scope.UserClaims.Count;
                    foreach (var t in claimTypes)
                        if (!scope.UserClaims.Contains(t, StringComparer.Ordinal))
                            scope.UserClaims.Add(t);
                    if (scope.UserClaims.Count != before) dirtyScopes.Add(scope);
                }
            }

            // Audience → migration-created clients requesting a member scope.
            foreach (var client in liveClients)
            {
                if (!_createdClientIds.Contains(client.ClientId)) continue;
                if (!client.AllowedScopes.Any(s => memberScopes.Contains(s, StringComparer.OrdinalIgnoreCase))) continue;
                if (client.Audiences.Contains(name, StringComparer.Ordinal)) continue;
                client.Audiences.Add(name);
                dirtyClients.Add(client);
            }

            report.ApiResourcesFlattened++;
        }

        if (!options.DryRun)
        {
            foreach (var scope in dirtyScopes) await stores.Scopes.UpdateAsync(scope, ct);
            foreach (var client in dirtyClients) await stores.Clients.UpsertAsync(client, ct);
        }
    }

    private static async Task<Dictionary<int, List<string>>> LoadIntStringMapAsync(
        SqlConnection sql, string table, string keyCol, string valueCol, CancellationToken ct)
    {
        var map = new Dictionary<int, List<string>>();
        if (!await sql.TableExistsAsync(table, ct))
            return map;

        await using var cmd = sql.CreateCommand();
        cmd.CommandText = $"SELECT {keyCol}, {valueCol} FROM {table}";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (reader.IsDBNull(1)) continue;
            var key = reader.GetInt32(0);
            if (!map.TryGetValue(key, out var list))
                map[key] = list = [];
            list.Add(reader.GetString(1));
        }
        return map;
    }

    // ---------------------------------------------------------------------------
    // 5. Roles
    // ---------------------------------------------------------------------------
    private async Task MigrateRolesAsync(
        SqlConnection sql, DuendeMigrationOptions options, Dictionary<string, string> idToName,
        DuendeMigrationReport report, DateTimeOffset now, CancellationToken ct)
    {
        if (!await sql.TableExistsAsync("AspNetRoles", ct))
            return;

        var existing = (await stores.Roles.ListAsync(ct)).Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var toCreate = new List<Role>();
        await using (var cmd = sql.CreateCommand())
        {
            cmd.CommandText = "SELECT Id, Name FROM AspNetRoles WHERE Name IS NOT NULL";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetString(0);
                var name = reader.GetString(1);
                idToName[id] = name;
                if (existing.Contains(name)) { report.RolesSkipped++; continue; }
                toCreate.Add(new Role { Id = Guid.NewGuid().ToString("N"), Name = name, CreatedAt = now });
            }
        }

        foreach (var role in toCreate)
        {
            if (!options.DryRun) await stores.Roles.CreateAsync(role, ct);
            report.RolesCreated++;
        }
    }

    // ---------------------------------------------------------------------------
    // 6. Users
    // ---------------------------------------------------------------------------
    private async Task MigrateUsersAsync(
        SqlConnection sql, DuendeMigrationOptions options, Dictionary<string, string> roleIdToName,
        DuendeMigrationReport report, DateTimeOffset now, CancellationToken ct)
    {
        if (!await sql.TableExistsAsync("AspNetUsers", ct))
            return;

        // user → role names
        var userRoles = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (await sql.TableExistsAsync("AspNetUserRoles", ct))
        {
            await using var cmd = sql.CreateCommand();
            cmd.CommandText = "SELECT UserId, RoleId FROM AspNetUserRoles";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var uid = reader.GetString(0);
                if (!roleIdToName.TryGetValue(reader.GetString(1), out var rname)) continue;
                if (!userRoles.TryGetValue(uid, out var list))
                    userRoles[uid] = list = [];
                list.Add(rname);
            }
        }

        // user → claims
        var userClaims = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        if (await sql.TableExistsAsync("AspNetUserClaims", ct))
        {
            await using var cmd = sql.CreateCommand();
            cmd.CommandText = "SELECT UserId, ClaimType, ClaimValue FROM AspNetUserClaims";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (reader.IsDBNull(0) || reader.IsDBNull(1)) continue;
                var uid = reader.GetString(0);
                if (!userClaims.TryGetValue(uid, out var map))
                    userClaims[uid] = map = new Dictionary<string, string>(StringComparer.Ordinal);
                map[reader.GetString(1)] = reader.IsDBNull(2) ? "" : reader.GetString(2);
            }
        }

        // Read + fold every user sequentially (single forward-only reader) into a work list; validation
        // and empty-email skips happen here on the reader thread. The writes then fan out below.
        var toWrite = new List<AuthUser>();
        await using (var cmd = sql.CreateCommand())
        {
            cmd.CommandText = """
                SELECT Id, UserName, Email, NormalizedEmail, EmailConfirmed, PasswordHash,
                       SecurityStamp, PhoneNumber, TwoFactorEnabled, LockoutEnd, LockoutEnabled, AccessFailedCount
                FROM AspNetUsers
                """;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetString(0);
                if (!DuendeMappings.IsValidUserId(id))
                {
                    report.InvalidUserIds.Add(id);
                    report.UsersSkipped++;
                    continue;
                }

                var email = reader.GetStringOrNull(2) ?? reader.GetStringOrNull(1) ?? "";
                if (string.IsNullOrWhiteSpace(email)) { report.UsersSkipped++; continue; }

                var user = new AuthUser
                {
                    Id = id,
                    Email = email,
                    NormalizedEmail = reader.GetStringOrNull(3) ?? email.ToUpperInvariant(),
                    EmailConfirmed = !reader.IsDBNull(4) && reader.GetBoolean(4),
                    PasswordHash = reader.GetStringOrNull(5),        // null is fine: external-SSO-only users
                    SecurityStamp = reader.GetStringOrNull(6) ?? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                    Phone = reader.GetStringOrNull(7),
                    MfaEnabled = !reader.IsDBNull(8) && reader.GetBoolean(8),
                    // AspNetUsers.LockoutEnd is datetimeoffset(7); read as such and keep the source offset.
                    LockoutEnd = reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9),
                    LockoutEnabled = !reader.IsDBNull(10) && reader.GetBoolean(10),
                    AccessFailedCount = reader.IsDBNull(11) ? 0 : reader.GetInt32(11),
                    CreatedAt = now,
                    IsActive = true,
                };

                if (userRoles.TryGetValue(id, out var roles)) user.Roles = roles;
                if (userClaims.TryGetValue(id, out var claims)) DuendeMappings.ApplyClaims(user, claims, overwrite: true);
                toWrite.Add(user);
            }
        }

        if (options.DryRun)
        {
            report.UsersCreated += toWrite.Count;
            return;
        }

        var created = 0;
        var updated = 0;
        var skipped = 0;
        await ForEachAsync(toWrite, options, async user =>
        {
            try
            {
                await stores.Users.CreateAsync(user, ct);
                Interlocked.Increment(ref created);
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 409)
            {
                if (options.UsersMode == UsersMode.Upsert)
                {
                    await stores.Users.UpdateAsync(user, ct);
                    Interlocked.Increment(ref updated);
                }
                else
                {
                    Interlocked.Increment(ref skipped);
                }
            }
        }, ct);

        report.UsersCreated += created;
        report.UsersUpdated += updated;
        report.UsersSkipped += skipped;
    }

    // ---------------------------------------------------------------------------
    // 7. External logins
    // ---------------------------------------------------------------------------
    private async Task MigrateExternalLoginsAsync(
        SqlConnection sql, DuendeMigrationOptions options, DuendeMigrationReport report, CancellationToken ct)
    {
        if (!await sql.TableExistsAsync("AspNetUserLogins", ct))
            return;

        var logins = new List<ExternalLoginInfo>();
        await using (var cmd = sql.CreateCommand())
        {
            cmd.CommandText = "SELECT LoginProvider, ProviderKey, ProviderDisplayName, UserId FROM AspNetUserLogins";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                logins.Add(new ExternalLoginInfo
                {
                    Provider = reader.GetString(0),
                    ProviderKey = reader.GetString(1),
                    DisplayName = reader.GetStringOrNull(2),
                    UserId = reader.GetString(3),
                });
            }
        }

        if (options.DryRun) { report.LoginsCreated += logins.Count; return; }

        var created = 0;
        var skipped = 0;
        await ForEachAsync(logins, options, async login =>
        {
            try
            {
                await stores.Users.AddLoginAsync(login, ct);
                Interlocked.Increment(ref created);
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 409)
            {
                Interlocked.Increment(ref skipped);
            }
        }, ct);

        report.LoginsCreated += created;
        report.LoginsSkipped += skipped;
    }

    // ---------------------------------------------------------------------------
    // 8. MFA — no-op on prod (zero rows), correct for dev/test/future.
    // ---------------------------------------------------------------------------
    private async Task MigrateMfaAsync(
        SqlConnection sql, DuendeMigrationOptions options, DuendeMigrationReport report, DateTimeOffset now, CancellationToken ct)
    {
        if (!await sql.TableExistsAsync("AspNetUserTokens", ct))
            return;

        // user → { AuthenticatorKey, RecoveryCodes }; LoginProvider ignored.
        var byUser = new Dictionary<string, (string? Key, string? Recovery)>(StringComparer.Ordinal);
        await using (var cmd = sql.CreateCommand())
        {
            cmd.CommandText = "SELECT UserId, Name, Value FROM AspNetUserTokens WHERE Name IN ('AuthenticatorKey', 'RecoveryCodes')";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (reader.IsDBNull(0) || reader.IsDBNull(1)) continue;
                var uid = reader.GetString(0);
                var name = reader.GetString(1);
                var value = reader.GetStringOrNull(2);
                byUser.TryGetValue(uid, out var entry);
                if (name == "AuthenticatorKey") entry.Key = value;
                else if (name == "RecoveryCodes") entry.Recovery = value;
                byUser[uid] = entry;
            }
        }

        var usersSkipped = 0;
        var credentialsCreated = 0;
        var warnings = new ConcurrentBag<string>();

        await ForEachAsync(byUser.ToList(), options, async entry =>
        {
            var userId = entry.Key;
            var tokens = entry.Value;

            // Skip if this user already has MFA credentials (idempotent re-run).
            if ((await stores.Mfa.GetCredentialsAsync(userId, ct)).Count > 0)
            {
                Interlocked.Increment(ref usersSkipped);
                return;
            }

            var credentials = new List<MfaCredential>();

            if (!string.IsNullOrWhiteSpace(tokens.Key))
            {
                byte[] secret;
                try
                {
                    secret = TotpService.Base32Decode(tokens.Key);
                }
                catch (FormatException)
                {
                    warnings.Add($"User {userId}: AuthenticatorKey is not valid base32 — TOTP not migrated.");
                    secret = [];
                }

                if (secret.Length > 0)
                {
                    var protectedSecret = options.DryRun
                        ? "(dry-run)"
                        : await secretProvider.ProtectAsync($"mfa-totp-{userId}", Convert.ToBase64String(secret), ct);

                    credentials.Add(new MfaCredential
                    {
                        Id = "duende-totp",
                        UserId = userId,
                        Type = MfaCredentialType.Totp,
                        Name = "Authenticator app",
                        SecretProtected = protectedSecret,
                        CreatedAt = now,
                    });
                }
            }

            if (!string.IsNullOrWhiteSpace(tokens.Recovery))
            {
                var codes = tokens.Recovery.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                for (var i = 0; i < codes.Length; i++)
                {
                    credentials.Add(new MfaCredential
                    {
                        Id = $"duende-rc-{i}",
                        UserId = userId,
                        Type = MfaCredentialType.RecoveryCode,
                        Name = $"Recovery code {i + 1}",
                        SecretProtected = recoveryCodes.HashForStorage(codes[i]),
                        CreatedAt = now,
                    });
                }
            }

            foreach (var credential in credentials)
            {
                if (!options.DryRun) await stores.Mfa.CreateCredentialAsync(credential, ct);
                Interlocked.Increment(ref credentialsCreated);
            }
        }, ct);

        report.MfaUsersSkipped += usersSkipped;
        report.MfaCredentialsCreated += credentialsCreated;
        foreach (var warning in warnings) report.Warnings.Add(warning);
    }

    // ---------------------------------------------------------------------------
    // 9. SAML / OIDC providers + SSO domains
    // ---------------------------------------------------------------------------
    private async Task MigrateProvidersAsync(
        SqlConnection sql, DuendeMigrationOptions options, DuendeMigrationReport report, CancellationToken ct)
    {
        if (await sql.TableExistsAsync("SamlProviderConfigurations", ct))
        {
            await using var cmd = sql.CreateCommand();
            cmd.CommandText = "SELECT Id, EntityId, MetadataLocation, AllowedDomains, ConnectionName FROM SamlProviderConfigurations";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var connectionId = $"saml-{reader.GetInt32(0)}";
                var config = new SamlProviderConfig
                {
                    ConnectionId = connectionId,
                    ConnectionName = reader.GetStringOrNull(4) ?? connectionId,
                    EntityId = reader.GetString(1),
                    MetadataLocation = reader.GetStringOrNull(2) ?? "",
                    AllowedDomains = SplitDomains(reader.GetStringOrNull(3)),
                };

                if (!options.DryRun) await stores.SamlProviders.UpsertAsync(config, ct);
                report.SamlProvidersCreated++;
                await UpsertSsoDomainsAsync(options, config.AllowedDomains, "saml", connectionId, report, ct);
            }
        }

        if (await sql.TableExistsAsync("OidcProviderConfigurations", ct))
        {
            await using var cmd = sql.CreateCommand();
            cmd.CommandText = "SELECT Id, MetadataLocation, ConnectionName, RedirectUrl, AllowedDomains, ClientId, ClientSecret FROM OidcProviderConfigurations";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var connectionId = $"oidc-{reader.GetInt32(0)}";
                var config = new OidcProviderConfig
                {
                    ConnectionId = connectionId,
                    ConnectionName = reader.GetStringOrNull(2) ?? connectionId,
                    MetadataLocation = reader.GetString(1),
                    RedirectUrl = reader.GetStringOrNull(3) ?? "",
                    ClientId = reader.GetString(5),
                    ClientSecret = reader.GetStringOrNull(6) ?? "",
                    AllowedDomains = SplitDomains(reader.GetStringOrNull(4)),
                };

                if (!options.DryRun) await stores.OidcProviders.UpsertAsync(config, ct);
                report.OidcProvidersCreated++;
                await UpsertSsoDomainsAsync(options, config.AllowedDomains, "oidc", connectionId, report, ct);
            }
        }
    }

    private async Task UpsertSsoDomainsAsync(
        DuendeMigrationOptions options, List<string> domains, string providerType, string connectionId,
        DuendeMigrationReport report, CancellationToken ct)
    {
        foreach (var domain in domains)
        {
            var ssoDomain = new SsoDomain
            {
                Domain = domain.ToLowerInvariant(),
                ProviderType = providerType,
                ConnectionId = connectionId,
                Scheme = $"{providerType}:{connectionId}",
            };
            if (!options.DryRun) await stores.SsoDomains.UpsertAsync(ssoDomain, ct);
            report.SsoDomainsCreated++;
        }
    }

    private static List<string> SplitDomains(string? raw)
        => (raw ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    // ---------------------------------------------------------------------------
    // 10. Refresh tokens (opt-in)
    // ---------------------------------------------------------------------------
    private async Task MigrateRefreshTokensAsync(
        SqlConnection sql, DuendeMigrationOptions options, DuendeMigrationReport report, CancellationToken ct)
    {
        if (!await sql.TableExistsAsync("PersistedGrants", ct))
            return;

        var grants = new List<PersistedGrant>();
        await using (var cmd = sql.CreateCommand())
        {
            cmd.CommandText = """
                SELECT [Key], [Type], SubjectId, ClientId, [Data], CreationTime, Expiration, ConsumedTime
                FROM PersistedGrants
                WHERE [Type] = 'refresh_token' AND (Expiration IS NULL OR Expiration > GETUTCDATE())
                """;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                grants.Add(new PersistedGrant
                {
                    Key = reader.GetStringOrNull(0) ?? Guid.NewGuid().ToString("N"),
                    Type = reader.GetString(1),
                    SubjectId = reader.GetStringOrNull(2),
                    ClientId = reader.GetString(3),
                    Data = reader.GetString(4),
                    CreatedAt = reader.GetDateTime(5),
                    ExpiresAt = reader.IsDBNull(6) ? DateTimeOffset.UtcNow.AddDays(30) : reader.GetDateTime(6),
                    ConsumedAt = reader.IsDBNull(7) ? null : reader.GetDateTime(7),
                });
            }
        }

        if (options.DryRun) { report.RefreshTokensCreated += grants.Count; return; }

        var created = 0;
        var skipped = 0;
        await ForEachAsync(grants, options, async grant =>
        {
            try
            {
                await stores.Grants.StoreAsync(grant, ct);
                Interlocked.Increment(ref created);
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 409)
            {
                Interlocked.Increment(ref skipped);
            }
        }, ct);

        report.RefreshTokensCreated += created;
        report.RefreshTokensSkipped += skipped;
    }
}
