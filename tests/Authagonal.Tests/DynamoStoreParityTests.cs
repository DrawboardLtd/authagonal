using Amazon.DynamoDBv2;
using Authagonal.AwsProvider.Dynamo;
using Authagonal.AwsProvider.Stores;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

/// <summary>
/// Behavioral parity coverage for the Dynamo config/lookup stores against real DynamoDB semantics
/// (DynamoDB Local) — the AWS counterpart of the Azurite entity/round-trip suites: CRUD round-trips
/// (including the newer SAML fields like SpCertificate/NameIdFormat), the by-name/by-hash/by-domain
/// lookups, env-partitioned scans, and the single-use OIDC state consume.
/// </summary>
[Collection("Dynamo")]
public class DynamoStoreParityTests(DynamoFixture dynamo)
{
    private readonly IAmazonDynamoDB _db = dynamo.CreateClient();

    private async Task<DynamoTable> T(string name)
    {
        await DynamoTableProvisioner.EnsureTableAsync(_db, name);
        return new DynamoTable(_db, name);
    }

    // ----- DynamoClientStore -----------------------------------------------------

    [Fact]
    public async Task ClientStore_UpsertGetGetAllDelete_RoundTrips()
    {
        var store = new DynamoClientStore(await T("pcClients"), EnvPartitioner.Live);

        var client = new OAuthClient
        {
            ClientId = "web-app",
            ClientName = "Web App",
            Description = "primary SPA",
            Enabled = true,
            ClientSecretHashes = ["hash-1"],
            AllowedGrantTypes = ["authorization_code", "refresh_token"],
            RedirectUris = ["https://app.example.com/callback"],
            PostLogoutRedirectUris = ["https://app.example.com/"],
            AllowedScopes = ["openid", "profile", "api"],
            AllowedCorsOrigins = ["https://app.example.com"],
            RequirePkce = true,
            AllowOfflineAccess = true,
            AccessTokenLifetimeSeconds = 900,
            RefreshTokenUsage = RefreshTokenUsage.ReUse,
            RefreshTokenExpiration = RefreshTokenExpiration.Sliding,
            MfaPolicy = MfaPolicy.Required,
            IsDefaultApplication = true,
            InitiateLoginUri = "https://app.example.com/login",
        };
        await store.UpsertAsync(client);
        await store.UpsertAsync(new OAuthClient { ClientId = "cli-tool", ClientName = "CLI" });

        var read = await store.GetAsync("web-app");
        Assert.NotNull(read);
        Assert.Equal("Web App", read!.ClientName);
        Assert.Equal(["authorization_code", "refresh_token"], read.AllowedGrantTypes);
        Assert.Equal(["https://app.example.com/callback"], read.RedirectUris);
        Assert.Equal(["openid", "profile", "api"], read.AllowedScopes);
        Assert.True(read.AllowOfflineAccess);
        Assert.Equal(900, read.AccessTokenLifetimeSeconds);
        Assert.Equal(RefreshTokenUsage.ReUse, read.RefreshTokenUsage);
        Assert.Equal(RefreshTokenExpiration.Sliding, read.RefreshTokenExpiration);
        Assert.Equal(MfaPolicy.Required, read.MfaPolicy);
        Assert.True(read.IsDefaultApplication);
        Assert.Equal("https://app.example.com/login", read.InitiateLoginUri);
        Assert.Null(await store.GetAsync("missing"));

        // Upsert replaces in place.
        client.ClientName = "Web App v2";
        await store.UpsertAsync(client);
        Assert.Equal("Web App v2", (await store.GetAsync("web-app"))?.ClientName);

        var all = await store.GetAllAsync();
        Assert.Equal(2, all.Count);
        Assert.Contains(all, c => c.ClientId == "cli-tool");

        await store.DeleteAsync("web-app");
        Assert.Null(await store.GetAsync("web-app"));
        Assert.Equal("cli-tool", Assert.Single(await store.GetAllAsync()).ClientId);

        await store.DeleteAsync("web-app"); // deleting a missing client is a no-op
    }

    [Fact]
    public async Task ClientStore_SandboxEnvs_ShareOneTable_WithoutLeaking()
    {
        // Two sandbox envs share one physical table; each env's GetAll is bounded to its pk range.
        var table = await T("pcClientsEnv");
        var testEnv = new DynamoClientStore(table, new EnvPartitioner("test"));
        var stagingEnv = new DynamoClientStore(table, new EnvPartitioner("staging"));

        await testEnv.UpsertAsync(new OAuthClient { ClientId = "shared-id", ClientName = "test copy" });
        await stagingEnv.UpsertAsync(new OAuthClient { ClientId = "shared-id", ClientName = "staging copy" });
        await stagingEnv.UpsertAsync(new OAuthClient { ClientId = "staging-only", ClientName = "s2" });

        Assert.Equal("test copy", (await testEnv.GetAsync("shared-id"))?.ClientName);
        Assert.Equal("staging copy", (await stagingEnv.GetAsync("shared-id"))?.ClientName);
        Assert.Null(await testEnv.GetAsync("staging-only"));

        Assert.Equal("shared-id", Assert.Single(await testEnv.GetAllAsync()).ClientId);
        Assert.Equal(2, (await stagingEnv.GetAllAsync()).Count);

        // Deleting in one env leaves the other env's row intact.
        await testEnv.DeleteAsync("shared-id");
        Assert.Null(await testEnv.GetAsync("shared-id"));
        Assert.Equal("staging copy", (await stagingEnv.GetAsync("shared-id"))?.ClientName);
    }

    // ----- DynamoSigningKeyStore -------------------------------------------------

    [Fact]
    public async Task SigningKeyStore_StoreListActivateDeactivateDelete()
    {
        var store = new DynamoSigningKeyStore(await T("pcSigningKeys"), EnvPartitioner.Live);
        Assert.Null(await store.GetActiveKeyAsync());
        Assert.Empty(await store.GetAllAsync());

        var created = DateTimeOffset.UtcNow;
        await store.StoreAsync(new SigningKeyInfo
        {
            KeyId = "key-old",
            Algorithm = "ES256",
            KeyMaterialJson = """{"Curve":"P-256","D":"old"}""",
            IsActive = false,
            CreatedAt = created.AddDays(-30),
            ExpiresAt = created.AddDays(-1),
        });
        await store.StoreAsync(new SigningKeyInfo
        {
            KeyId = "key-active",
            Algorithm = "ES256",
            KeyMaterialJson = """{"Curve":"P-256","D":"live"}""",
            IsActive = true,
            CreatedAt = created,
            ExpiresAt = created.AddDays(90),
        });

        var active = await store.GetActiveKeyAsync();
        Assert.Equal("key-active", active?.KeyId);
        Assert.Equal("ES256", active!.Algorithm);
        Assert.Equal("""{"Curve":"P-256","D":"live"}""", active.KeyMaterialJson);
        Assert.True(active.IsActive);
        Assert.Equal(created.ToUniversalTime(), active.CreatedAt); // stored as round-trip "O" UTC

        Assert.Equal(2, (await store.GetAllAsync()).Count);

        await store.DeactivateKeyAsync("key-active");
        Assert.Null(await store.GetActiveKeyAsync());
        Assert.False((await store.GetAllAsync()).Single(k => k.KeyId == "key-active").IsActive);
        await store.DeactivateKeyAsync("missing"); // no-op

        await store.DeleteAsync("key-old");
        Assert.Equal("key-active", Assert.Single(await store.GetAllAsync()).KeyId);
    }

    // ----- DynamoRevokedTokenStore -----------------------------------------------

    [Fact]
    public async Task RevokedTokenStore_RevocationCountsOnlyUntilNaturalExpiry()
    {
        var store = new DynamoRevokedTokenStore(await T("pcRevoked"), EnvPartitioner.Live);

        await store.AddAsync("jti-live", DateTimeOffset.UtcNow.AddHours(1), clientId: "client-a");
        Assert.True(await store.IsRevokedAsync("jti-live"));
        Assert.False(await store.IsRevokedAsync("jti-unknown"));

        // Past the token's natural expiry the entry no longer reports revoked.
        await store.AddAsync("jti-stale", DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.False(await store.IsRevokedAsync("jti-stale"));

        // Blank jtis are ignored on write and read as not revoked.
        await store.AddAsync("", DateTimeOffset.UtcNow.AddHours(1));
        Assert.False(await store.IsRevokedAsync(""));
        Assert.False(await store.IsRevokedAsync("   "));
    }

    // ----- DynamoScopeStore ------------------------------------------------------

    [Fact]
    public async Task ScopeStore_CrudAndList()
    {
        var store = new DynamoScopeStore(await T("pcScopes"), EnvPartitioner.Live);

        await store.CreateAsync(new Scope
        {
            Name = "api.read",
            DisplayName = "Read the API",
            Description = "read-only access",
            Emphasize = true,
            Required = false,
            ShowInDiscoveryDocument = true,
            UserClaims = ["email", "role"],
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await store.CreateAsync(new Scope { Name = "api.write", CreatedAt = DateTimeOffset.UtcNow });

        var read = await store.GetAsync("api.read");
        Assert.NotNull(read);
        Assert.Equal("Read the API", read!.DisplayName);
        Assert.True(read.Emphasize);
        Assert.Equal(["email", "role"], read.UserClaims);
        Assert.Null(await store.GetAsync("missing"));

        read.DisplayName = "Read v2";
        read.UpdatedAt = DateTimeOffset.UtcNow;
        await store.UpdateAsync(read);
        var updated = await store.GetAsync("api.read");
        Assert.Equal("Read v2", updated?.DisplayName);
        Assert.NotNull(updated!.UpdatedAt);

        Assert.Equal(["api.read", "api.write"], (await store.ListAsync()).Select(s => s.Name).OrderBy(n => n));

        await store.DeleteAsync("api.read");
        Assert.Null(await store.GetAsync("api.read"));
        Assert.Equal("api.write", Assert.Single(await store.ListAsync()).Name);
    }

    // ----- DynamoRoleStore -------------------------------------------------------

    [Fact]
    public async Task RoleStore_CrudList_AndByNameLookup()
    {
        var store = new DynamoRoleStore(await T("pcRoles"), EnvPartitioner.Live);

        await store.CreateAsync(new Role { Id = "r1", Name = "tenant:admin", Description = "full control", CreatedAt = DateTimeOffset.UtcNow });
        await store.CreateAsync(new Role { Id = "r2", Name = "tenant:viewer", CreatedAt = DateTimeOffset.UtcNow });

        Assert.Equal("tenant:admin", (await store.GetAsync("r1"))?.Name);
        Assert.Null(await store.GetAsync("missing"));

        var byName = await store.GetByNameAsync("tenant:viewer");
        Assert.Equal("r2", byName?.Id);
        Assert.Null(await store.GetByNameAsync("tenant:nobody"));

        Assert.Equal(2, (await store.ListAsync()).Count);

        // Rename: the by-name lookup follows the promoted roleName attribute.
        var role = (await store.GetAsync("r2"))!;
        role.Name = "tenant:auditor";
        role.UpdatedAt = DateTimeOffset.UtcNow;
        await store.UpdateAsync(role);
        Assert.Null(await store.GetByNameAsync("tenant:viewer"));
        Assert.Equal("r2", (await store.GetByNameAsync("tenant:auditor"))?.Id);

        await store.DeleteAsync("r1");
        Assert.Null(await store.GetAsync("r1"));
        Assert.Null(await store.GetByNameAsync("tenant:admin"));
        Assert.Equal("r2", Assert.Single(await store.ListAsync()).Id);
    }

    // ----- DynamoScimTokenStore --------------------------------------------------

    [Fact]
    public async Task ScimTokenStore_DualIndex_StaysInSync_ThroughRevokeAndDelete()
    {
        var store = new DynamoScimTokenStore(await T("pcScimTokens"), EnvPartitioner.Live);

        static ScimToken Token(string id, string client) => new()
        {
            TokenId = id,
            ClientId = client,
            TokenHash = $"hash-{id}",
            Description = $"token {id}",
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        };

        await store.StoreAsync(Token("t1", "client-a"));
        await store.StoreAsync(Token("t2", "client-a"));
        await store.StoreAsync(Token("t3", "client-b"));

        var found = await store.FindByHashAsync("hash-t1");
        Assert.Equal("t1", found?.TokenId);
        Assert.Equal("client-a", found?.ClientId);
        Assert.False(found!.IsRevoked);
        Assert.Null(await store.FindByHashAsync("hash-none"));

        var byClient = await store.GetByClientAsync("client-a");
        Assert.Equal(["t1", "t2"], byClient.Select(t => t.TokenId).OrderBy(x => x));
        Assert.Equal("t3", Assert.Single(await store.GetByClientAsync("client-b")).TokenId);

        // Revoke rewrites both the forward (hash) row and the reverse (client) row.
        await store.RevokeAsync("t1", "client-a");
        Assert.True((await store.FindByHashAsync("hash-t1"))?.IsRevoked);
        Assert.True((await store.GetByClientAsync("client-a")).Single(t => t.TokenId == "t1").IsRevoked);
        await store.RevokeAsync("missing", "client-a"); // no-op

        // Delete removes both rows.
        await store.DeleteAsync("t1", "client-a");
        Assert.Null(await store.FindByHashAsync("hash-t1"));
        Assert.Equal("t2", Assert.Single(await store.GetByClientAsync("client-a")).TokenId);
        await store.DeleteAsync("t1", "client-a"); // already gone — no-op
    }

    // ----- DynamoScimGroupStore --------------------------------------------------

    [Fact]
    public async Task ScimGroupStore_CrudExternalIdLookup_AndMembership()
    {
        var store = new DynamoScimGroupStore(await T("pcScimGroups"), await T("pcScimGroupExtIds"), EnvPartitioner.Live);

        await store.CreateAsync(new ScimGroup
        {
            Id = "g1",
            DisplayName = "Engineering",
            ExternalId = "ext-eng",
            OrganizationId = "org-1",
            MemberUserIds = ["u1", "u2"],
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await store.CreateAsync(new ScimGroup
        {
            Id = "g2",
            DisplayName = "Sales",
            OrganizationId = "org-1",
            MemberUserIds = ["u2"],
            CreatedAt = DateTimeOffset.UtcNow,
        });

        var read = await store.GetAsync("g1");
        Assert.Equal("Engineering", read?.DisplayName);
        Assert.Equal(["u1", "u2"], read!.MemberUserIds);
        Assert.Null(await store.GetAsync("missing"));

        Assert.Equal("g1", (await store.FindByExternalIdAsync("org-1", "ext-eng"))?.Id);
        Assert.Null(await store.FindByExternalIdAsync("org-1", "ext-none"));
        Assert.Null(await store.FindByExternalIdAsync("org-2", "ext-eng")); // scoped to the org

        Assert.Equal(["g1", "g2"], (await store.GetGroupsByUserIdAsync("u2")).Select(g => g.Id).OrderBy(x => x));
        Assert.Equal("g1", Assert.Single(await store.GetGroupsByUserIdAsync("u1")).Id);
        Assert.Empty(await store.GetGroupsByUserIdAsync("u-none"));

        // Update that changes the external id drops the stale index entry.
        var g1 = (await store.GetAsync("g1"))!;
        g1.ExternalId = "ext-eng-2";
        g1.MemberUserIds = ["u1"];
        await store.UpdateAsync(g1);
        Assert.Null(await store.FindByExternalIdAsync("org-1", "ext-eng"));
        Assert.Equal("g1", (await store.FindByExternalIdAsync("org-1", "ext-eng-2"))?.Id);
        Assert.DoesNotContain(await store.GetGroupsByUserIdAsync("u2"), g => g.Id == "g1"); // u2 was removed from g1

        // Update of a missing group creates it.
        await store.UpdateAsync(new ScimGroup { Id = "g3", DisplayName = "Upserted", CreatedAt = DateTimeOffset.UtcNow });
        Assert.Equal("Upserted", (await store.GetAsync("g3"))?.DisplayName);

        // Delete removes the group and its external-id index entry.
        await store.DeleteAsync("g1");
        Assert.Null(await store.GetAsync("g1"));
        Assert.Null(await store.FindByExternalIdAsync("org-1", "ext-eng-2"));
        await store.DeleteAsync("g1"); // no-op
    }

    [Fact]
    public async Task ScimGroupStore_List_FiltersByOrg_AndPagesInCreatedAtOrder()
    {
        var store = new DynamoScimGroupStore(await T("pcScimGroupsL"), await T("pcScimGroupsLExtIds"), EnvPartitioner.Live);

        var t0 = DateTimeOffset.UtcNow;
        for (var i = 0; i < 5; i++)
        {
            await store.CreateAsync(new ScimGroup
            {
                Id = $"g{i}",
                DisplayName = $"Group {i}",
                OrganizationId = "org-1",
                CreatedAt = t0.AddMinutes(i),
            });
        }
        await store.CreateAsync(new ScimGroup { Id = "gx", DisplayName = "Other org", OrganizationId = "org-2", CreatedAt = t0 });

        // SCIM startIndex is 1-based; results order by CreatedAt.
        var (page, total) = await store.ListAsync("org-1", startIndex: 3, count: 2);
        Assert.Equal(5, total);
        Assert.Equal(["g2", "g3"], page.Select(g => g.Id));

        var (all, allTotal) = await store.ListAsync(null, startIndex: 1, count: 100);
        Assert.Equal(6, allTotal);
        Assert.Equal(6, all.Count);

        var (none, noneTotal) = await store.ListAsync("org-nope", startIndex: 1, count: 10);
        Assert.Empty(none);
        Assert.Equal(0, noneTotal);
    }

    // ----- DynamoScimGroupRoleMappingStore ----------------------------------------

    [Fact]
    public async Task ScimGroupRoleMappingStore_SetGetAllDelete_WithKeyUnsafeRoleNames()
    {
        var store = new DynamoScimGroupRoleMappingStore(await T("pcScimMappings"), EnvPartitioner.Live);
        Assert.Empty(await store.GetAllAsync());

        // Role names may contain characters unsafe for keys — the sk is a hash of (groupId, role).
        await store.SetAsync(new ScimGroupRoleMapping { GroupId = "g1", Role = "tenant:admin", GroupDisplayName = "Engineering" });
        await store.SetAsync(new ScimGroupRoleMapping { GroupId = "g1", Role = "weird role|with/unsafe chars?" });
        await store.SetAsync(new ScimGroupRoleMapping { GroupId = "g2", Role = "tenant:admin" });

        var all = await store.GetAllAsync();
        Assert.Equal(3, all.Count);
        Assert.Contains(all, m => m is { GroupId: "g1", Role: "weird role|with/unsafe chars?" });
        Assert.Equal("Engineering", all.Single(m => m is { GroupId: "g1", Role: "tenant:admin" }).GroupDisplayName);

        // Set is an upsert per (group, role).
        await store.SetAsync(new ScimGroupRoleMapping { GroupId = "g1", Role = "tenant:admin", GroupDisplayName = "Engineering v2" });
        all = await store.GetAllAsync();
        Assert.Equal(3, all.Count);
        Assert.Equal("Engineering v2", all.Single(m => m is { GroupId: "g1", Role: "tenant:admin" }).GroupDisplayName);

        await store.DeleteAsync("g1", "weird role|with/unsafe chars?");
        all = await store.GetAllAsync();
        Assert.Equal(2, all.Count);
        Assert.DoesNotContain(all, m => m.Role.StartsWith("weird", StringComparison.Ordinal));
        await store.DeleteAsync("g1", "not-mapped"); // no-op
    }

    // ----- DynamoOidcProviderStore -----------------------------------------------

    [Fact]
    public async Task OidcProviderStore_ConfigRoundTrip_GetAllDelete()
    {
        var store = new DynamoOidcProviderStore(await T("pcOidcProviders"), EnvPartitioner.Live);

        var config = new OidcProviderConfig
        {
            ConnectionId = "conn-okta",
            ConnectionName = "Okta",
            IconUrl = "https://cdn.example.com/okta.svg",
            MetadataLocation = "https://example.okta.com/.well-known/openid-configuration",
            ClientId = "okta-client",
            ClientSecret = "vault:v1:ciphertext",
            RedirectUrl = "https://auth.example.com/oidc/callback",
            AllowedDomains = ["example.com", "example.org"],
            DisableJitProvisioning = true,
            SessionExpClaim = "idp_session_exp",
            PassthroughParams = ["link_token"],
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await store.UpsertAsync(config);
        await store.UpsertAsync(new OidcProviderConfig { ConnectionId = "conn-google", ConnectionName = "Google" });

        var read = await store.GetAsync("conn-okta");
        Assert.NotNull(read);
        Assert.Equal("Okta", read!.ConnectionName);
        Assert.Equal("https://cdn.example.com/okta.svg", read.IconUrl);
        Assert.Equal("vault:v1:ciphertext", read.ClientSecret);
        Assert.Equal(["example.com", "example.org"], read.AllowedDomains);
        Assert.True(read.DisableJitProvisioning);
        Assert.Equal("idp_session_exp", read.SessionExpClaim);
        Assert.Equal(["link_token"], read.PassthroughParams);
        Assert.Null(await store.GetAsync("missing"));

        Assert.Equal(2, (await store.GetAllAsync()).Count);

        await store.DeleteAsync("conn-okta");
        Assert.Null(await store.GetAsync("conn-okta"));
        Assert.Equal("conn-google", Assert.Single(await store.GetAllAsync()).ConnectionId);
    }

    // ----- DynamoSamlProviderStore -----------------------------------------------

    [Fact]
    public async Task SamlProviderStore_ConfigRoundTrip_IncludingSpCertificateAndNameIdFormat()
    {
        var store = new DynamoSamlProviderStore(await T("pcSamlProviders"), EnvPartitioner.Live);

        var config = new SamlProviderConfig
        {
            ConnectionId = "conn-adfs",
            ConnectionName = "Contoso ADFS",
            IconUrl = "https://cdn.example.com/adfs.svg",
            EntityId = "https://auth.example.com/saml/conn-adfs/metadata",
            MetadataLocation = "https://adfs.contoso.com/FederationMetadata/2007-06/FederationMetadata.xml",
            MetadataXml = "<EntityDescriptor entityID=\"https://adfs.contoso.com/adfs/services/trust\"/>",
            NameIdFormat = "none", // the ADFS-safe "omit NameIDPolicy" setting
            SpCertificate = Convert.ToBase64String([1, 2, 3, 4, 5]), // protected PKCS#12 blob — must survive verbatim
            SignAuthnRequests = true,
            AllowedDomains = ["contoso.com"],
            DisableJitProvisioning = false,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await store.UpsertAsync(config);

        var read = await store.GetAsync("conn-adfs");
        Assert.NotNull(read);
        Assert.Equal("Contoso ADFS", read!.ConnectionName);
        Assert.Equal(config.EntityId, read.EntityId);
        Assert.Equal(config.MetadataXml, read.MetadataXml);
        Assert.Equal("none", read.NameIdFormat);
        Assert.Equal(config.SpCertificate, read.SpCertificate);
        Assert.True(read.SignAuthnRequests);
        Assert.Equal(["contoso.com"], read.AllowedDomains);
        Assert.Null(await store.GetAsync("missing"));

        // Nullable fields stay null when unset (no defaulting on the read path).
        await store.UpsertAsync(new SamlProviderConfig { ConnectionId = "conn-bare", ConnectionName = "Bare" });
        var bare = await store.GetAsync("conn-bare");
        Assert.Null(bare!.NameIdFormat);
        Assert.Null(bare.SpCertificate);
        Assert.Null(bare.SignAuthnRequests);
        Assert.Null(bare.MetadataXml);

        Assert.Equal(2, (await store.GetAllAsync()).Count);

        await store.DeleteAsync("conn-adfs");
        Assert.Null(await store.GetAsync("conn-adfs"));
        Assert.Equal("conn-bare", Assert.Single(await store.GetAllAsync()).ConnectionId);
    }

    // ----- DynamoSsoDomainStore --------------------------------------------------

    [Fact]
    public async Task SsoDomainStore_DomainRouting_CaseInsensitive_AndDeleteByConnection()
    {
        var store = new DynamoSsoDomainStore(await T("pcSsoDomains"), EnvPartitioner.Live);

        await store.UpsertAsync(new SsoDomain { Domain = "Contoso.COM", ProviderType = "saml", ConnectionId = "conn-adfs", Scheme = "saml:conn-adfs" });
        await store.UpsertAsync(new SsoDomain { Domain = "contoso.net", ProviderType = "saml", ConnectionId = "conn-adfs", Scheme = "saml:conn-adfs" });
        await store.UpsertAsync(new SsoDomain { Domain = "fabrikam.com", ProviderType = "oidc", ConnectionId = "conn-okta", Scheme = "oidc:conn-okta" });

        // The routing lookup is case-insensitive on the domain (lower-cased pk).
        var routed = await store.GetAsync("CONTOSO.com");
        Assert.Equal("conn-adfs", routed?.ConnectionId);
        Assert.Equal("saml", routed?.ProviderType);
        Assert.Equal("saml:conn-adfs", routed?.Scheme);
        Assert.Null(await store.GetAsync("nowhere.example"));

        Assert.Equal(3, (await store.GetAllAsync()).Count);

        // Removing a connection removes every domain routed to it — and only those.
        await store.DeleteByConnectionAsync("conn-adfs");
        Assert.Null(await store.GetAsync("contoso.com"));
        Assert.Null(await store.GetAsync("contoso.net"));
        Assert.Equal("conn-okta", Assert.Single(await store.GetAllAsync()).ConnectionId);

        await store.DeleteAsync("fabrikam.com");
        Assert.Empty(await store.GetAllAsync());
        await store.DeleteAsync("fabrikam.com"); // no-op
    }

    // ----- DynamoUserProvisionStore ----------------------------------------------

    [Fact]
    public async Task UserProvisionStore_StoreListRemove_AndRemoveAllByUser()
    {
        var store = new DynamoUserProvisionStore(await T("pcUserProvisions"), EnvPartitioner.Live);

        var t = DateTimeOffset.UtcNow;
        await store.StoreAsync(new UserProvision { UserId = "u1", AppId = "app-a", ProvisionedAt = t });
        await store.StoreAsync(new UserProvision { UserId = "u1", AppId = "app-b", ProvisionedAt = t.AddMinutes(1) });
        await store.StoreAsync(new UserProvision { UserId = "u2", AppId = "app-a", ProvisionedAt = t });

        var u1 = await store.GetByUserAsync("u1");
        Assert.Equal(["app-a", "app-b"], u1.Select(p => p.AppId).OrderBy(x => x));
        Assert.All(u1, p => Assert.Equal("u1", p.UserId));
        Assert.Empty(await store.GetByUserAsync("u-none"));

        await store.RemoveAsync("u1", "app-a");
        Assert.Equal("app-b", Assert.Single(await store.GetByUserAsync("u1")).AppId);
        await store.RemoveAsync("u1", "app-a"); // no-op

        await store.RemoveAllByUserAsync("u1");
        Assert.Empty(await store.GetByUserAsync("u1"));
        Assert.Single(await store.GetByUserAsync("u2")); // untouched
        await store.RemoveAllByUserAsync("u-none"); // no-op
    }

    // ----- DynamoOidcStateStore --------------------------------------------------

    [Fact]
    public async Task OidcStateStore_StoreConsume_IsStrictlySingleUse()
    {
        var store = new DynamoOidcStateStore(await T("pcOidcState"), TimeSpan.FromMinutes(10));

        await store.StoreAsync("state-1", "conn-okta", "/portal/home", "verifier-abc", "nonce-xyz");
        var data = await store.ConsumeAsync("state-1");
        Assert.NotNull(data);
        Assert.Equal("conn-okta", data!.ConnectionId);
        Assert.Equal("/portal/home", data.ReturnUrl);
        Assert.Equal("verifier-abc", data.CodeVerifier);
        Assert.Equal("nonce-xyz", data.Nonce);

        Assert.Null(await store.ConsumeAsync("state-1")); // consumed — gone
        Assert.Null(await store.ConsumeAsync("state-never-stored"));

        // Exactly one concurrent consumer may win (conditional delete-returning).
        await store.StoreAsync("state-2", "conn-okta", "/", "v", "n");
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => store.ConsumeAsync("state-2")));
        Assert.Equal(1, results.Count(r => r is not null));
    }

    [Fact]
    public async Task OidcStateStore_ExpiredState_IsRejected_AndStillConsumed()
    {
        var store = new DynamoOidcStateStore(await T("pcOidcStateTtl"), TimeSpan.FromMilliseconds(50));

        await store.StoreAsync("state-old", "conn-a", "/", "v", "n");
        await Task.Delay(200);

        Assert.Null(await store.ConsumeAsync("state-old")); // expired
        Assert.Null(await store.ConsumeAsync("state-old")); // and the row was deleted by the attempt
    }
}
