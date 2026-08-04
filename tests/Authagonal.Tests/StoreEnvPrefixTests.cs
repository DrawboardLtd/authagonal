using Authagonal.AzureProvider.Stores;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Tests.Infrastructure;
using Azure.Data.Tables;

namespace Authagonal.Tests;

/// <summary>
/// The same env-prefix round trip <see cref="MfaEnvPrefixTests"/> pins, on the six stores that never had it.
/// </summary>
/// <remarks>
/// <c>EnvPartitioner.Strip</c> was applied on the MFA and user read paths and on no others, while nine Azure
/// entities set a model IDENTITY field from the raw <c>PartitionKey</c>. Outside the live env that key carries
/// <c>{env}|</c>, so those six stores handed back <c>dev|natural</c> — and every one of those fields is fed
/// straight back into <c>PK()</c> on the next write, which prefixes it again and lands the row at
/// <c>dev|dev|natural</c>. The write reports success and changes nothing.
/// <para>
/// Consequences that are not cosmetic, all on non-live environments:
/// </para>
/// <list type="bullet">
/// <item>
/// <b>Revoking trust in a compromised IdP silently did nothing.</b> The admin SSO update reads the connection,
/// mutates it and upserts — so replacing a compromised IdP's <c>MetadataXml</c> (i.e. rotating the trusted
/// assertion-signing certificate), clearing <c>AllowUninvitedJit</c> or trimming <c>AllowedDomains</c> returned
/// 200, wrote an audit row, and left the SAML endpoints reading the unchanged values off the real row.
/// </item>
/// <item>
/// <b>Group-to-role mapping never applied at all.</b> <c>UserStoreOidcSubjectResolver</c> built member group
/// ids from <c>ScimGroup.Id</c> (<c>dev|g1</c>) and compared them against <c>ScimGroupRoleMapping.GroupId</c>
/// (<c>g1</c>), so the sets never intersected and no group ever granted a role.
/// </item>
/// <item>
/// <b>SCIM group updates forked the row.</b> Update probed the doubly-prefixed key, missed, and fell through
/// to create — leaving a phantom, repointing the external-id index at it, and burning the per-client quota.
/// </item>
/// </list>
/// <para>
/// The strip now lives inside each <c>ToModel</c> as a required parameter rather than as a convention at the
/// call site, which is what makes it unforgettable — the compiler found 22 sites, three of which had no strip
/// and were not on the original list.
/// </para>
/// <para>
/// Against Azurite rather than a fake, for the reason <see cref="MfaEnvPrefixTests"/> gives: an in-memory
/// store keyed on a tuple cannot express the defect at all, which is why the shared parity suite missed it.
/// </para>
/// </remarks>
[Collection("Azurite")]
public class StoreEnvPrefixTests(AzuriteFixture azurite)
{
    private readonly TableServiceClient _svc = new(azurite.ConnectionString);
    private static readonly EnvPartitioner Dev = new("dev");

    private TableClient T(string prefix, string name)
    {
        var c = _svc.GetTableClient($"{prefix}{name}");
        c.CreateIfNotExists();
        return c;
    }

    private static string Prefix() => $"envpfx{Guid.NewGuid():N}"[..20];

    [Fact]
    public async Task ClientReadBackCarriesTheNaturalClientId_AndAWriteThroughItLands()
    {
        var p = Prefix();
        var store = new TableClientStore(T(p, "Clients"), Dev);

        await store.UpsertAsync(new OAuthClient { ClientId = "web", ClientName = "Web" });

        var read = await store.GetAsync("web");
        Assert.Equal("web", read!.ClientId);

        // The read-modify-write an admin update performs. Doubly prefixed, this created a phantom and the
        // real row kept its old name.
        read.ClientName = "Renamed";
        await store.UpsertAsync(read);

        Assert.Equal("Renamed", (await store.GetAsync("web"))!.ClientName);
        Assert.Single(await store.GetAllAsync());
    }

    [Fact]
    public async Task ScimGroupReadBackCarriesTheNaturalId_AndAWriteThroughItLands()
    {
        var p = Prefix();
        var store = new TableScimGroupStore(
            T(p, "ScimGroups"), T(p, "ScimGroupExternalIds"), Dev);

        await store.CreateAsync(new ScimGroup
        {
            Id = "g1", DisplayName = "Eng", OrganizationId = "org1",
        });

        var read = await store.GetAsync("g1");
        Assert.Equal("g1", read!.Id);

        read.DisplayName = "Engineering";
        await store.UpdateAsync(read);

        Assert.Equal("Engineering", (await store.GetAsync("g1"))!.DisplayName);

        // One row, not the original plus a phantom — the fall-through to CreateAsync is what burned the
        // per-client group quota.
        var (groups, total) = await store.ListAsync("org1", 1, 50);
        Assert.Single(groups);
        Assert.Equal(1, total);
    }

    [Fact]
    public async Task OidcConnectionReadBackCarriesTheNaturalConnectionId_AndAWriteThroughItLands()
    {
        var p = Prefix();
        var store = new TableOidcProviderStore(T(p, "OidcProviders"), Dev);

        await store.UpsertAsync(new OidcProviderConfig
        {
            ConnectionId = "acme", ConnectionName = "Acme",
            MetadataLocation = "https://idp.example/.well-known/openid-configuration",
            ClientId = "c", ClientSecret = "s",
        });

        var read = await store.GetAsync("acme");
        Assert.Equal("acme", read!.ConnectionId);

        read.ConnectionName = "Acme Corp";
        await store.UpsertAsync(read);

        Assert.Equal("Acme Corp", (await store.GetAsync("acme"))!.ConnectionName);
        Assert.Single(await store.GetAllAsync());
    }

    /// <summary>
    /// The SAML one is the security-relevant instance: this is how trust in a compromised IdP is revoked.
    /// </summary>
    [Fact]
    public async Task SamlConnectionReadBackCarriesTheNaturalConnectionId_AndRotatingItsTrustLands()
    {
        var p = Prefix();
        var store = new TableSamlProviderStore(T(p, "SamlProviders"), Dev);

        await store.UpsertAsync(new SamlProviderConfig
        {
            ConnectionId = "acme", ConnectionName = "Acme", EntityId = "urn:acme",
            MetadataXml = "<compromised/>", AllowUninvitedJit = true,
        });

        var read = await store.GetAsync("acme");
        Assert.Equal("acme", read!.ConnectionId);

        // Exactly what an operator revoking trust does: replace the signing metadata, close the JIT door.
        read.MetadataXml = "<rotated/>";
        read.AllowUninvitedJit = false;
        await store.UpsertAsync(read);

        var reread = await store.GetAsync("acme");
        Assert.Equal("<rotated/>", reread!.MetadataXml);
        Assert.False(reread.AllowUninvitedJit);
        Assert.Single(await store.GetAllAsync());
    }

    [Fact]
    public async Task UserProvisionReadBackCarriesTheNaturalUserId()
    {
        var p = Prefix();
        var store = new TableUserProvisionStore(T(p, "UserProvisions"), Dev);

        await store.StoreAsync(new UserProvision
        {
            UserId = "user-42", AppId = "app-1", ProvisionedAt = DateTimeOffset.UtcNow,
        });

        var all = await store.GetByUserAsync("user-42");
        Assert.Equal("user-42", Assert.Single(all).UserId);
    }

    [Fact]
    public async Task UserReadBackCarriesTheNaturalId_OnEveryReadPath()
    {
        var p = Prefix();
        var store = new TableUserStore(
            T(p, "Users"), T(p, "UserEmails"), T(p, "UserNames"), T(p, "UserExternalLogins"),
            null, null, Dev);

        await store.CreateAsync(new AuthUser
        {
            Id = "user-42", Email = "a@x.example", NormalizedEmail = "A@X.EXAMPLE",
        });

        Assert.Equal("user-42", (await store.GetAsync("user-42"))!.Id);
        Assert.Equal("user-42", (await store.FindByEmailAsync("a@x.example"))!.Id);

        // The search path had no strip at all — it was not on the original finding's list and the compiler
        // surfaced it.
        Assert.Equal("user-42", Assert.Single(await store.SearchAsync("a@x.example")).Id);
    }
}
