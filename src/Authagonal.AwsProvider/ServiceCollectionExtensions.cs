using Amazon.DynamoDBv2;
using Amazon.SecretsManager;
using Authagonal.AwsProvider.Dynamo;
using Authagonal.AwsProvider.Secrets;
using Authagonal.AwsProvider.Stores;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Authagonal.AwsProvider;

/// <summary>
/// DI entry points for the AWS backend — the counterpart to <c>Authagonal.AzureProvider.AddTableStorage</c>.
/// The DynamoDB client resolves credentials via the standard AWS chain (env / EC2 instance role / IRSA),
/// so there is no managed-identity-vs-connection-string split like Azure's: pass a configured
/// <see cref="IAmazonDynamoDB"/> (or <see cref="IAmazonSecretsManager"/>) and go.
/// </summary>
public static class ServiceCollectionExtensions
{
    // Table names mirror the Azure layout one-for-one.
    private const string ClientsTable = "Clients";
    private const string GrantsTable = "Grants";
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
    private const string RolesTable = "Roles";
    private const string ScopesTable = "Scopes";
    private const string RevokedTokensTable = "RevokedTokens";
    private const string ProvisioningAppsTable = "ProvisioningApps";
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

    // Default TTLs for the transient SAML-replay / OIDC-state caches (the Azure path reads these from
    // CacheOptions, which lives in Authagonal.Server; for the single-tenant AWS host these defaults match).
    private static readonly TimeSpan SamlReplayTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan OidcStateTtl = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Registers the DynamoDB-backed stores. Eagerly ensures the tables exist (idempotent — a no-op when
    /// they're already provisioned by Terraform). Single-env hosts use the live partitioner.
    /// </summary>
    /// <param name="nameIndexesEnabled">
    /// When false, the UserFirstNames / UserLastNames tables are neither provisioned nor written, and
    /// <see cref="IUserStore.SearchAsync"/> degrades from "email + name prefix" to "email prefix only"
    /// — mirroring the Azure overload.
    /// </param>
    /// <remarks>
    /// Implements the full <c>Authagonal.Core.Stores</c> surface: tombstone writer, client,
    /// signing-key, grant, user, role, scope, revoked-token, provisioning-app, user-provision,
    /// OIDC/SAML provider, SSO-domain, SCIM token/group/role-mapping, and MFA stores.
    /// </remarks>
    public static IServiceCollection AddDynamoStorage(this IServiceCollection services, IAmazonDynamoDB db, bool nameIndexesEnabled = true)
    {
        var tables = new List<string>
        {
            ClientsTable, GrantsTable, GrantsBySubjectTable, GrantsByExpiryTable, SigningKeysTable, TombstonesTable,
            UsersTable, UserEmailsTable, UserLoginsTable, UserExternalIdsTable,
            RolesTable, ScopesTable, RevokedTokensTable, ProvisioningAppsTable, UserProvisionsTable,
            OidcProvidersTable, SamlProvidersTable, SsoDomainsTable,
            ScimTokensTable, ScimGroupsTable, ScimGroupExternalIdsTable, ScimGroupRoleMappingsTable,
            MfaCredentialsTable, MfaChallengesTable, MfaWebAuthnIndexTable,
            SamlReplayCacheTable, OidcStateStoreTable,
        };
        if (nameIndexesEnabled) tables.AddRange([UserFirstNamesTable, UserLastNamesTable]);
        foreach (var table in tables)
            DynamoTableProvisioner.EnsureTableAsync(db, table).GetAwaiter().GetResult();

        var live = EnvPartitioner.Live;
        var tombstones = new DynamoChangeWriter(new DynamoTable(db, TombstonesTable));
        services.TryAddSingleton<IChangeWriter>(tombstones);

        services.TryAddSingleton<IClientStore>(new DynamoClientStore(new DynamoTable(db, ClientsTable), live, tombstones));
        services.TryAddSingleton<ISigningKeyStore>(new DynamoSigningKeyStore(new DynamoTable(db, SigningKeysTable), live, tombstones));
        services.TryAddSingleton<IGrantStore>(sp => new DynamoGrantStore(
            new DynamoTable(db, GrantsTable),
            new DynamoTable(db, GrantsBySubjectTable),
            new DynamoTable(db, GrantsByExpiryTable),
            live,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<DynamoGrantStore>(),
            tombstones));
        services.TryAddSingleton<IUserStore>(new DynamoUserStore(
            new DynamoTable(db, UsersTable),
            new DynamoTable(db, UserEmailsTable),
            new DynamoTable(db, UserLoginsTable),
            new DynamoTable(db, UserExternalIdsTable),
            nameIndexesEnabled ? new DynamoTable(db, UserFirstNamesTable) : null,
            nameIndexesEnabled ? new DynamoTable(db, UserLastNamesTable) : null,
            live,
            tombstones));
        services.TryAddSingleton<IRoleStore>(new DynamoRoleStore(new DynamoTable(db, RolesTable), live, tombstones));
        services.TryAddSingleton<IScopeStore>(new DynamoScopeStore(new DynamoTable(db, ScopesTable), live, tombstones));
        services.TryAddSingleton<IRevokedTokenStore>(new DynamoRevokedTokenStore(new DynamoTable(db, RevokedTokensTable), live));
        services.TryAddSingleton<IProvisioningAppStore>(new DynamoProvisioningAppStore(new DynamoTable(db, ProvisioningAppsTable), live, tombstones));
        services.TryAddSingleton<IUserProvisionStore>(new DynamoUserProvisionStore(new DynamoTable(db, UserProvisionsTable), live, tombstones));
        services.TryAddSingleton<IOidcProviderStore>(new DynamoOidcProviderStore(new DynamoTable(db, OidcProvidersTable), live, tombstones));
        services.TryAddSingleton<ISamlProviderStore>(new DynamoSamlProviderStore(new DynamoTable(db, SamlProvidersTable), live, tombstones));
        services.TryAddSingleton<ISsoDomainStore>(new DynamoSsoDomainStore(new DynamoTable(db, SsoDomainsTable), live, tombstones));
        services.TryAddSingleton<IScimTokenStore>(new DynamoScimTokenStore(new DynamoTable(db, ScimTokensTable), live, tombstones));
        services.TryAddSingleton<IScimGroupStore>(new DynamoScimGroupStore(new DynamoTable(db, ScimGroupsTable), new DynamoTable(db, ScimGroupExternalIdsTable), live, tombstones));
        services.TryAddSingleton<IScimGroupRoleMappingStore>(new DynamoScimGroupRoleMappingStore(new DynamoTable(db, ScimGroupRoleMappingsTable), live));
        services.TryAddSingleton<IMfaStore>(new DynamoMfaStore(
            new DynamoTable(db, MfaCredentialsTable),
            new DynamoTable(db, MfaChallengesTable),
            new DynamoTable(db, MfaWebAuthnIndexTable),
            live,
            tombstones));

        // Transient replay/state caches (the SAML + OIDC-federation seams). Registered behind the Core
        // interfaces so Authagonal.Server's endpoints resolve them regardless of backend.
        services.TryAddSingleton<ISamlReplayCache>(new DynamoSamlReplayCache(new DynamoTable(db, SamlReplayCacheTable), SamlReplayTtl));
        services.TryAddSingleton<IOidcStateStore>(new DynamoOidcStateStore(new DynamoTable(db, OidcStateStoreTable), OidcStateTtl));

        return services;
    }

    /// <summary>
    /// Registers AWS Secrets Manager as the <see cref="ISecretProvider"/> (substitute for Azure Key
    /// Vault). Replaces any existing provider (e.g. the plaintext default).
    /// </summary>
    public static IServiceCollection AddSecretsManager(this IServiceCollection services, IAmazonSecretsManager client)
    {
        services.TryAddSingleton(client);
        services.Replace(ServiceDescriptor.Singleton<ISecretProvider, SecretsManagerSecretProvider>());
        return services;
    }
}
