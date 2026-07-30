using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.SqlProvider.Sql;
using Authagonal.SqlProvider.Stores;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Authagonal.SqlProvider;

/// <summary>
/// DI entry points for the self-hosted SQL backend — the counterpart to
/// <c>Authagonal.AzureProvider.AddTableStorage</c> and <c>Authagonal.AwsProvider.AddDynamoStorage</c>.
/// Call before <c>AddAuthagonal</c>: the registrations here are what make <c>AddAuthagonal</c> skip
/// its Azure Table wiring.
/// <code>
/// builder.Services.AddAuthagonalPostgres("Host=db;Database=authagonal;Username=auth;Password=…");
/// builder.Services.AddAuthagonal(builder.Configuration);
/// </code>
/// </summary>
public static class ServiceCollectionExtensions
{
    // Table names mirror the Azure and AWS layouts one-for-one, so a backup taken on one backend
    // restores onto another without renaming anything.
    private const string ClientsTable = "Clients";
    private const string GrantsTable = "Grants";
    private const string UpstreamRefreshTokensTable = "UpstreamRefreshTokens";
    private const string GrantsBySubjectTable = "GrantsBySubject";
    private const string GrantsByExpiryTable = "GrantsByExpiry";
    private const string SigningKeysTable = "SigningKeys";
    private const string TombstonesTable = "Tombstones";
    private const string UsersTable = "Users";
    private const string UserEmailsTable = "UserEmails";
    private const string UserLoginsTable = "UserLogins";
    private const string UserExternalIdsTable = "UserExternalIds";
    private const string UserFirstNamesTable = "UserFirstNames";
    private const string UserLastNamesTable = "UserLastNames";
    private const string UserEmailDomainsTable = "UserEmailDomains";
    private const string UserEmailLocalPrefixesTable = "UserEmailLocalPrefixes";
    private const string RolesTable = "Roles";
    private const string ScopesTable = "Scopes";
    private const string RevokedTokensTable = "RevokedTokens";
    private const string ProvisioningAppsTable = "ProvisioningApps";
    private const string AgentProfilesTable = "AgentProfiles";
    private const string UserProvisionsTable = "UserProvisions";
    private const string OidcProvidersTable = "OidcProviders";
    private const string SamlProvidersTable = "SamlProviders";
    private const string SsoDomainsTable = "SsoDomains";
    private const string ScimTokensTable = "ScimTokens";
    private const string ScimGroupsTable = "ScimGroups";
    private const string ScimGroupExternalIdsTable = "ScimGroupExternalIds";
    private const string ScimGroupRoleMappingsTable = "ScimGroupRoleMappings";
    private const string MfaCredentialsTable = "MfaCredentials";
    private const string MfaChallengesTable = "MfaChallenges";
    private const string MfaWebAuthnIndexTable = "MfaWebAuthnIndex";
    private const string SamlReplayCacheTable = "SamlReplayCache";
    private const string OidcStateStoreTable = "OidcStateStore";

    // Defaults for the transient SAML-replay / OIDC-state caches (the Azure path reads these from
    // CacheOptions, which lives in Authagonal.Server; for a single-tenant host these defaults match).
    private static readonly TimeSpan SamlReplayTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan OidcStateTtl = TimeSpan.FromMinutes(10);

    /// <summary>How often <see cref="SqlExpiryReaper"/> sweeps rows past their TTL.</summary>
    private static readonly TimeSpan ExpirySweepInterval = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Registers the SQL-backed stores against an already-built <see cref="SqlDataSource"/>. Eagerly
    /// provisions the tables (idempotent — a no-op when they already exist, and safe to race across
    /// pods since every DDL statement is IF NOT EXISTS).
    /// </summary>
    /// <param name="nameIndexesEnabled">
    /// When false, the UserFirstNames / UserLastNames tables are neither provisioned nor written, and
    /// <see cref="IUserStore.SearchAsync"/> degrades from "email + name prefix" to "email prefix only"
    /// — mirroring the Azure and AWS overloads.
    /// </param>
    public static IServiceCollection AddSqlStorage(
        this IServiceCollection services, SqlDataSource source, bool nameIndexesEnabled = true)
    {
        var names = new List<string>
        {
            ClientsTable, GrantsTable, GrantsBySubjectTable, GrantsByExpiryTable, SigningKeysTable, TombstonesTable,
            UsersTable, UserEmailsTable, UserLoginsTable, UserExternalIdsTable,
            UserEmailDomainsTable, UserEmailLocalPrefixesTable,
            RolesTable, ScopesTable, RevokedTokensTable, ProvisioningAppsTable, AgentProfilesTable, UserProvisionsTable,
            OidcProvidersTable, SamlProvidersTable, SsoDomainsTable, UpstreamRefreshTokensTable,
            ScimTokensTable, ScimGroupsTable, ScimGroupExternalIdsTable, ScimGroupRoleMappingsTable,
            MfaCredentialsTable, MfaChallengesTable, MfaWebAuthnIndexTable,
            SamlReplayCacheTable, OidcStateStoreTable,
        };
        if (nameIndexesEnabled) names.AddRange([UserFirstNamesTable, UserLastNamesTable]);

        var tables = source.EnsureTablesAsync(names).GetAwaiter().GetResult();
        SqlTable T(string name) => tables[name];

        services.TryAddSingleton(source);

        var live = EnvPartitioner.Live;
        var tombstones = new SqlChangeWriter(T(TombstonesTable));
        services.TryAddSingleton<IChangeWriter>(tombstones);

        services.TryAddSingleton<IClientStore>(new SqlClientStore(T(ClientsTable), live, tombstones));
        services.TryAddSingleton<ISigningKeyStore>(new SqlSigningKeyStore(T(SigningKeysTable), live, tombstones));
        // The crypto seams (IFieldCipher / IIndexTokenizer) resolve lazily from the container so a host
        // that registers them BEFORE AddSqlStorage gets PII encryption + blind-index keys — the Null
        // passthroughs apply otherwise (plaintext, the default layout).
        services.TryAddSingleton<IGrantStore>(sp => new SqlGrantStore(
            T(GrantsTable),
            T(GrantsBySubjectTable),
            T(GrantsByExpiryTable),
            live,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<SqlGrantStore>(),
            tombstones,
            sp.GetService<IFieldCipher>()));
        services.TryAddSingleton<IUserStore>(sp => new SqlUserStore(
            T(UsersTable),
            T(UserEmailsTable),
            T(UserLoginsTable),
            T(UserExternalIdsTable),
            nameIndexesEnabled ? T(UserFirstNamesTable) : null,
            nameIndexesEnabled ? T(UserLastNamesTable) : null,
            live,
            tombstones,
            T(UserEmailDomainsTable),
            T(UserEmailLocalPrefixesTable),
            sp.GetService<IFieldCipher>(),
            sp.GetService<IIndexTokenizer>()));
        services.TryAddSingleton<IRoleStore>(new SqlRoleStore(T(RolesTable), live, tombstones));
        services.TryAddSingleton<IScopeStore>(new SqlScopeStore(T(ScopesTable), live, tombstones));
        services.TryAddSingleton<IRevokedTokenStore>(new SqlRevokedTokenStore(T(RevokedTokensTable), live));
        services.TryAddSingleton<IProvisioningAppStore>(sp => new SqlProvisioningAppStore(
            T(ProvisioningAppsTable), live, tombstones, sp.GetService<IFieldCipher>()));
        services.TryAddSingleton<IAgentProfileStore>(new SqlAgentProfileStore(T(AgentProfilesTable), live, tombstones));
        services.TryAddSingleton<IUserProvisionStore>(new SqlUserProvisionStore(T(UserProvisionsTable), live, tombstones));
        services.TryAddSingleton<IOidcProviderStore>(new SqlOidcProviderStore(T(OidcProvidersTable), live, tombstones));
        services.TryAddSingleton<IUpstreamRefreshTokenStore>(sp => new SqlUpstreamRefreshTokenStore(
            T(UpstreamRefreshTokensTable), live, sp.GetService<IFieldCipher>()));
        services.TryAddSingleton<ISamlProviderStore>(new SqlSamlProviderStore(T(SamlProvidersTable), live, tombstones));
        services.TryAddSingleton<ISsoDomainStore>(new SqlSsoDomainStore(T(SsoDomainsTable), live, tombstones));
        services.TryAddSingleton<IScimTokenStore>(new SqlScimTokenStore(T(ScimTokensTable), live, tombstones));
        services.TryAddSingleton<IScimGroupStore>(new SqlScimGroupStore(
            T(ScimGroupsTable), T(ScimGroupExternalIdsTable), live, tombstones));
        services.TryAddSingleton<IScimGroupRoleMappingStore>(new SqlScimGroupRoleMappingStore(T(ScimGroupRoleMappingsTable), live));
        services.TryAddSingleton<IMfaStore>(new SqlMfaStore(
            T(MfaCredentialsTable), T(MfaChallengesTable), T(MfaWebAuthnIndexTable), live, tombstones));

        // Transient replay/state caches (the SAML + OIDC-federation seams). Registered behind the Core
        // interfaces so Authagonal.Server's endpoints resolve them regardless of backend.
        services.TryAddSingleton<ISamlReplayCache>(new SqlSamlReplayCache(T(SamlReplayCacheTable), SamlReplayTtl));
        services.TryAddSingleton<IOidcStateStore>(new SqlOidcStateStore(T(OidcStateStoreTable), OidcStateTtl));

        // Neither backend expires rows on its own, so the TTL-bearing tables need an explicit sweeper
        // (see SqlExpiryReaper for why grants are deliberately not in this list).
        SqlTable[] expiring =
        [
            T(SamlReplayCacheTable), T(OidcStateStoreTable), T(MfaChallengesTable),
            T(UpstreamRefreshTokensTable), T(RevokedTokensTable),
        ];
        services.AddSingleton<IHostedService>(sp => new SqlExpiryReaper(
            expiring, ExpirySweepInterval, sp.GetRequiredService<ILogger<SqlExpiryReaper>>()));

        return services;
    }

    /// <summary>
    /// Registers the SQL stores when <c>Storage:Provider</c> asks for them, and does nothing at all
    /// otherwise — so a host can call this unconditionally before <c>AddAuthagonal</c> and let
    /// configuration pick the backend. This is how the shipped image supports
    /// <c>Storage__Provider=postgres</c> with no code change, while keeping the PostgreSQL and SQLite
    /// drivers out of the <c>Authagonal.Server</c> package for consumers who don't want them.
    /// <list type="bullet">
    /// <item><c>postgres</c> — needs <c>Storage:ConnectionString</c>; honours <c>Storage:Schema</c>
    /// (default <c>public</c>).</item>
    /// <item><c>sqlite</c> — <c>Storage:ConnectionString</c> defaults to <c>Data Source=authagonal.db</c>,
    /// so a bare <c>docker run</c> comes up with durable state instead of failing.</item>
    /// <item>anything else (including unset) — no-op; <c>AddAuthagonal</c> proceeds to its Azure wiring.</item>
    /// </list>
    /// </summary>
    /// <returns>The service collection, so this composes in a chain.</returns>
    public static IServiceCollection AddAuthagonalSqlStorageFromConfiguration(
        this IServiceCollection services, IConfiguration configuration)
    {
        // Read directly rather than via the configuration binder, so this package needs only
        // Configuration.Abstractions. Unset or unparseable means enabled, matching AddAuthagonal.
        var nameIndexesEnabled = !bool.TryParse(configuration["Storage:NameIndexesEnabled"], out var enabled) || enabled;
        var connectionString = configuration["Storage:ConnectionString"];

        switch (configuration["Storage:Provider"]?.Trim().ToLowerInvariant())
        {
            case "postgres" or "postgresql":
                if (string.IsNullOrWhiteSpace(connectionString))
                    throw new InvalidOperationException(
                        "Storage:ConnectionString must be configured for Storage:Provider=postgres.");
                return services.AddAuthagonalPostgres(
                    connectionString, configuration["Storage:Schema"] ?? "public", nameIndexesEnabled);

            case "sqlite":
                return services.AddAuthagonalSqlite(
                    string.IsNullOrWhiteSpace(connectionString) ? "Data Source=authagonal.db" : connectionString,
                    nameIndexesEnabled);

            default:
                return services;
        }
    }

    /// <summary>
    /// One-call composition for a PostgreSQL host: stores, DataProtection key ring, and (optionally)
    /// the schema the tables live in. Call BEFORE <c>AddAuthagonal</c>.
    /// </summary>
    public static IServiceCollection AddAuthagonalPostgres(
        this IServiceCollection services,
        string connectionString,
        string schema = "public",
        bool nameIndexesEnabled = true,
        bool persistDataProtectionKeys = true)
        => services.AddAuthagonalSqlStorage(
            new SqlDataSource(new PostgresDialect(connectionString, schema)), nameIndexesEnabled, persistDataProtectionKeys);

    /// <summary>
    /// One-call composition for a SQLite host — one file, no server. Suits the quick start, embedded
    /// library hosts and CI; a multi-pod deployment wants <see cref="AddAuthagonalPostgres"/>.
    /// Call BEFORE <c>AddAuthagonal</c>.
    /// </summary>
    public static IServiceCollection AddAuthagonalSqlite(
        this IServiceCollection services,
        string connectionString,
        bool nameIndexesEnabled = true,
        bool persistDataProtectionKeys = true)
        => services.AddAuthagonalSqlStorage(
            new SqlDataSource(new SqliteDialect(connectionString)), nameIndexesEnabled, persistDataProtectionKeys);

    /// <summary>
    /// Stores plus the SQL-backed DataProtection key ring, against a data source you built yourself
    /// (a custom dialect, a tuned connection string, a shared instance).
    /// </summary>
    public static IServiceCollection AddAuthagonalSqlStorage(
        this IServiceCollection services,
        SqlDataSource source,
        bool nameIndexesEnabled = true,
        bool persistDataProtectionKeys = true)
    {
        services.AddSqlStorage(source, nameIndexesEnabled);
        if (persistDataProtectionKeys)
            services.PersistDataProtectionKeysToSql(source);
        return services;
    }
}
