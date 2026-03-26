using System.Security.Cryptography;
using Authagonal.Core.Models;
using Authagonal.Core.Stores;
using Authagonal.Migration;
using Authagonal.Storage;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

// ---------------------------------------------------------------------------
// Configuration
// ---------------------------------------------------------------------------
var config = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: true)
    .AddCommandLine(args)
    .Build();

var sqlConnectionString = config["Source:ConnectionString"]
    ?? throw new InvalidOperationException("Source:ConnectionString is required (--Source:ConnectionString \"...\")");

var tableStorageConnectionString = config["Target:ConnectionString"]
    ?? throw new InvalidOperationException("Target:ConnectionString is required (--Target:ConnectionString \"...\")");

var dryRun = config.GetValue("DryRun", false);
var migrateRefreshTokens = config.GetValue("MigrateRefreshTokens", false);

Console.WriteLine($"Source: SQL Server");
Console.WriteLine($"Target: Azure Table Storage");
Console.WriteLine($"Dry run: {dryRun}");
Console.WriteLine($"Migrate refresh tokens: {migrateRefreshTokens}");
Console.WriteLine();

// ---------------------------------------------------------------------------
// Setup stores
// ---------------------------------------------------------------------------
var stores = StoreFactory.Create(tableStorageConnectionString);

await using var sql = new SqlConnection(sqlConnectionString);
await sql.OpenAsync();

var stats = new MigrationStats();

// ---------------------------------------------------------------------------
// 1. Users + UserClaims
// ---------------------------------------------------------------------------
Console.WriteLine("=== Migrating Users ===");

await using (var cmd = sql.CreateCommand())
{
    cmd.CommandText = """
        SELECT u.Id, u.Email, u.NormalizedEmail, u.EmailConfirmed, u.PasswordHash,
               u.SecurityStamp, u.PhoneNumber, u.LockoutEnd, u.LockoutEnabled,
               u.AccessFailedCount,
               fn.ClaimValue AS FirstName,
               ln.ClaimValue AS LastName,
               cn.ClaimValue AS CompanyName,
               oid.ClaimValue AS OrganizationId
        FROM AspNetUsers u
        LEFT JOIN AspNetUserClaims fn  ON fn.UserId = u.Id  AND fn.ClaimType = 'given_name'
        LEFT JOIN AspNetUserClaims ln  ON ln.UserId = u.Id  AND ln.ClaimType = 'family_name'
        LEFT JOIN AspNetUserClaims cn  ON cn.UserId = u.Id  AND cn.ClaimType = 'company'
        LEFT JOIN AspNetUserClaims oid ON oid.UserId = u.Id AND oid.ClaimType = 'org_id'
        """;

    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        var user = new AuthUser
        {
            Id = reader.GetString(0),
            Email = reader.GetStringOrNull(1) ?? "",
            NormalizedEmail = reader.GetStringOrNull(2) ?? "",
            EmailConfirmed = reader.GetBoolean(3),
            PasswordHash = reader.GetStringOrNull(4),
            SecurityStamp = reader.GetStringOrNull(5)
                ?? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            Phone = reader.GetStringOrNull(6),
            LockoutEnd = reader.IsDBNull(7) ? null : reader.GetDateTimeOffset(7),
            LockoutEnabled = reader.GetBoolean(8),
            AccessFailedCount = reader.GetInt32(9),
            FirstName = reader.GetStringOrNull(10),
            LastName = reader.GetStringOrNull(11),
            CompanyName = reader.GetStringOrNull(12),
            OrganizationId = reader.GetStringOrNull(13),
            CreatedAt = DateTimeOffset.UtcNow
        };

        if (!dryRun)
        {
            try
            {
                await stores.UserStore.CreateAsync(user);
                stats.UsersCreated++;
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 409)
            {
                await stores.UserStore.UpdateAsync(user);
                stats.UsersUpdated++;
            }
        }
        else
        {
            stats.UsersCreated++;
        }
    }
}

Console.WriteLine($"  Users created: {stats.UsersCreated}, updated: {stats.UsersUpdated}");

// ---------------------------------------------------------------------------
// 2. External Logins
// ---------------------------------------------------------------------------
Console.WriteLine("=== Migrating External Logins ===");

await using (var cmd = sql.CreateCommand())
{
    cmd.CommandText = "SELECT LoginProvider, ProviderKey, ProviderDisplayName, UserId FROM AspNetUserLogins";

    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        var login = new ExternalLoginInfo
        {
            Provider = reader.GetString(0),
            ProviderKey = reader.GetString(1),
            DisplayName = reader.GetStringOrNull(2),
            UserId = reader.GetString(3)
        };

        if (!dryRun)
        {
            try
            {
                await stores.UserStore.AddLoginAsync(login);
                stats.LoginsCreated++;
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 409)
            {
                stats.LoginsSkipped++;
            }
        }
        else
        {
            stats.LoginsCreated++;
        }
    }
}

Console.WriteLine($"  Logins created: {stats.LoginsCreated}, skipped: {stats.LoginsSkipped}");

// ---------------------------------------------------------------------------
// 3. SAML Providers -> SamlProviders + SsoDomains
// ---------------------------------------------------------------------------
Console.WriteLine("=== Migrating SAML Providers ===");

await using (var cmd = sql.CreateCommand())
{
    cmd.CommandText = "SELECT Id, EntityId, MetadataLocation, AllowedDomains, ConnectionName FROM SamlProviderConfigurations";

    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        var id = reader.GetInt32(0);
        var connectionId = $"saml-{id}";
        var allowedDomains = reader.GetStringOrNull(3) ?? "";

        var samlConfig = new SamlProviderConfig
        {
            ConnectionId = connectionId,
            ConnectionName = reader.GetStringOrNull(4) ?? connectionId,
            EntityId = reader.GetString(1),
            MetadataLocation = reader.GetStringOrNull(2) ?? "",
            AllowedDomains = allowedDomains.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
        };

        if (!dryRun)
        {
            await stores.SamlProviderStore.UpsertAsync(samlConfig);

            foreach (var domain in samlConfig.AllowedDomains)
            {
                var ssoDomain = new SsoDomain
                {
                    Domain = domain.ToLowerInvariant(),
                    ProviderType = "saml",
                    ConnectionId = connectionId,
                    Scheme = $"saml:{connectionId}"
                };
                await stores.SsoDomainStore.UpsertAsync(ssoDomain);
                stats.SsoDomainsCreated++;
            }
        }

        stats.SamlProvidersCreated++;
    }
}

Console.WriteLine($"  SAML providers: {stats.SamlProvidersCreated}, SSO domains: {stats.SsoDomainsCreated}");

// ---------------------------------------------------------------------------
// 4. OIDC Providers -> OidcProviders + SsoDomains
// ---------------------------------------------------------------------------
Console.WriteLine("=== Migrating OIDC Providers ===");

await using (var cmd = sql.CreateCommand())
{
    cmd.CommandText = "SELECT Id, MetadataLocation, ConnectionName, RedirectUrl, AllowedDomains, ClientId, ClientSecret FROM OidcProviderConfigurations";

    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        var id = reader.GetInt32(0);
        var connectionId = $"oidc-{id}";
        var allowedDomains = reader.GetStringOrNull(4) ?? "";

        var oidcConfig = new OidcProviderConfig
        {
            ConnectionId = connectionId,
            ConnectionName = reader.GetStringOrNull(2) ?? connectionId,
            MetadataLocation = reader.GetString(1),
            RedirectUrl = reader.GetStringOrNull(3) ?? "",
            ClientId = reader.GetString(5),
            ClientSecret = reader.GetStringOrNull(6) ?? "",
            AllowedDomains = allowedDomains.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
        };

        if (!dryRun)
        {
            await stores.OidcProviderStore.UpsertAsync(oidcConfig);

            foreach (var domain in oidcConfig.AllowedDomains)
            {
                var ssoDomain = new SsoDomain
                {
                    Domain = domain.ToLowerInvariant(),
                    ProviderType = "oidc",
                    ConnectionId = connectionId,
                    Scheme = $"oidc:{connectionId}"
                };
                await stores.SsoDomainStore.UpsertAsync(ssoDomain);
                stats.SsoDomainsCreated++;
            }
        }

        stats.OidcProvidersCreated++;
    }
}

Console.WriteLine($"  OIDC providers: {stats.OidcProvidersCreated}");

// ---------------------------------------------------------------------------
// 5. Clients (from Duende ConfigurationDb)
// ---------------------------------------------------------------------------
Console.WriteLine("=== Migrating Clients ===");

await using (var cmd = sql.CreateCommand())
{
    cmd.CommandText = """
        SELECT c.ClientId, c.ClientName, c.RequirePkce, c.AllowOfflineAccess,
               c.RequireClientSecret, c.AlwaysIncludeUserClaimsInIdToken,
               c.AccessTokenLifetime, c.IdentityTokenLifetime, c.AuthorizationCodeLifetime,
               c.AbsoluteRefreshTokenLifetime, c.SlidingRefreshTokenLifetime,
               c.RefreshTokenUsage
        FROM Clients c
        """;

    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        var clientId = reader.GetString(0);

        var client = new OAuthClient
        {
            ClientId = clientId,
            ClientName = reader.GetStringOrNull(1) ?? clientId,
            RequirePkce = reader.GetBoolean(2),
            AllowOfflineAccess = reader.GetBoolean(3),
            RequireClientSecret = reader.GetBoolean(4),
            AlwaysIncludeUserClaimsInIdToken = reader.GetBoolean(5),
            AccessTokenLifetimeSeconds = reader.GetInt32(6),
            IdentityTokenLifetimeSeconds = reader.GetInt32(7),
            AuthorizationCodeLifetimeSeconds = reader.GetInt32(8),
            AbsoluteRefreshTokenLifetimeSeconds = reader.GetInt32(9),
            SlidingRefreshTokenLifetimeSeconds = reader.GetInt32(10),
            RefreshTokenUsage = (RefreshTokenUsage)reader.GetInt32(11)
        };

        // Load related data from child tables
        client.ClientSecretHashes = await LoadClientStrings(sql, clientId,
            "SELECT Value FROM ClientSecrets WHERE ClientId = (SELECT Id FROM Clients WHERE ClientId = @cid)");
        client.AllowedGrantTypes = await LoadClientStrings(sql, clientId,
            "SELECT GrantType FROM ClientGrantTypes WHERE ClientId = (SELECT Id FROM Clients WHERE ClientId = @cid)");
        client.RedirectUris = await LoadClientStrings(sql, clientId,
            "SELECT RedirectUri FROM ClientRedirectUris WHERE ClientId = (SELECT Id FROM Clients WHERE ClientId = @cid)");
        client.PostLogoutRedirectUris = await LoadClientStrings(sql, clientId,
            "SELECT PostLogoutRedirectUri FROM ClientPostLogoutRedirectUris WHERE ClientId = (SELECT Id FROM Clients WHERE ClientId = @cid)");
        client.AllowedScopes = await LoadClientStrings(sql, clientId,
            "SELECT Scope FROM ClientScopes WHERE ClientId = (SELECT Id FROM Clients WHERE ClientId = @cid)");
        client.AllowedCorsOrigins = await LoadClientStrings(sql, clientId,
            "SELECT Origin FROM ClientCorsOrigins WHERE ClientId = (SELECT Id FROM Clients WHERE ClientId = @cid)");

        if (!dryRun)
            await stores.ClientStore.UpsertAsync(client);

        stats.ClientsCreated++;
    }
}

Console.WriteLine($"  Clients: {stats.ClientsCreated}");

// ---------------------------------------------------------------------------
// 6. Refresh Tokens (optional)
// ---------------------------------------------------------------------------
if (migrateRefreshTokens)
{
    Console.WriteLine("=== Migrating Refresh Tokens ===");

    await using var cmd = sql.CreateCommand();
    cmd.CommandText = """
        SELECT [Key], [Type], SubjectId, ClientId, [Data], CreationTime, Expiration, ConsumedTime
        FROM PersistedGrants
        WHERE [Type] = 'refresh_token' AND (Expiration IS NULL OR Expiration > GETUTCDATE())
        """;

    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        var grant = new PersistedGrant
        {
            Key = reader.GetStringOrNull(0) ?? Guid.NewGuid().ToString("N"),
            Type = reader.GetString(1),
            SubjectId = reader.GetStringOrNull(2),
            ClientId = reader.GetString(3),
            Data = reader.GetString(4),
            CreatedAt = reader.GetDateTime(5),
            ExpiresAt = reader.IsDBNull(6) ? DateTimeOffset.UtcNow.AddDays(30) : reader.GetDateTime(6),
            ConsumedAt = reader.IsDBNull(7) ? null : reader.GetDateTime(7)
        };

        if (!dryRun)
        {
            try
            {
                await stores.GrantStore.StoreAsync(grant);
                stats.RefreshTokensCreated++;
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 409)
            {
                stats.RefreshTokensSkipped++;
            }
        }
        else
        {
            stats.RefreshTokensCreated++;
        }
    }

    Console.WriteLine($"  Refresh tokens created: {stats.RefreshTokensCreated}, skipped: {stats.RefreshTokensSkipped}");
}

// ---------------------------------------------------------------------------
// Done
// ---------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("=== Migration Complete ===");
Console.WriteLine($"  Users:           {stats.UsersCreated} created, {stats.UsersUpdated} updated");
Console.WriteLine($"  External logins: {stats.LoginsCreated} created, {stats.LoginsSkipped} skipped");
Console.WriteLine($"  SAML providers:  {stats.SamlProvidersCreated}");
Console.WriteLine($"  OIDC providers:  {stats.OidcProvidersCreated}");
Console.WriteLine($"  SSO domains:     {stats.SsoDomainsCreated}");
Console.WriteLine($"  Clients:         {stats.ClientsCreated}");
if (migrateRefreshTokens)
    Console.WriteLine($"  Refresh tokens:  {stats.RefreshTokensCreated} created, {stats.RefreshTokensSkipped} skipped");

if (dryRun)
    Console.WriteLine("\n  ** DRY RUN -- no data was written **");

return 0;

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------
static async Task<List<string>> LoadClientStrings(SqlConnection sql, string clientId, string query)
{
    await using var cmd = sql.CreateCommand();
    cmd.CommandText = query;
    cmd.Parameters.AddWithValue("@cid", clientId);

    var results = new List<string>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        var val = reader.GetStringOrNull(0);
        if (val is not null)
            results.Add(val);
    }
    return results;
}
