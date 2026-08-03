using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Authagonal.Core.Models;
using Authagonal.Protocol;
using Authagonal.Protocol.Services;
using Authagonal.Core.Services;
using Authagonal.SqlProvider.Sql;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

/// <summary>
/// Cross-backend invariants that only one provider actually held, plus the two key-lifecycle windows
/// where a token was minted that nothing could verify.
/// </summary>
public class StorageParityTests
{
    // -----------------------------------------------------------------------
    // F109 — one definition of "which domain is this address in"
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("bob@corp.com", "corp.com")]
    [InlineData("BOB@CORP.COM", "corp.com")]
    // The whole defect: the SSO gates read everything after the FIRST '@' and got "x@corp.com",
    // which matches no registered SSO domain — so forced SSO never fired — while the storage layer
    // filed the account under corp.com.
    [InlineData("bob@x@corp.com", "corp.com")]
    [InlineData("no-at-sign", null)]
    [InlineData("trailing@", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void EmailDomain_AlwaysTakesTheLastAtSign(string? email, string? expected)
    {
        Assert.Equal(expected, EmailDomain.Of(email));
    }

    [Theory]
    [InlineData("bob@corp.com", true)]
    [InlineData("bob@x@corp.com", false)]
    [InlineData("@corp.com", false)]
    [InlineData("bob@", false)]
    [InlineData("bob", false)]
    public void EmailDomain_RejectsAmbiguousAddresses(string email, bool expected)
    {
        Assert.Equal(expected, EmailDomain.HasUnambiguousDomain(email));
    }

    // -----------------------------------------------------------------------
    // F236 — prefix upper bound over scalar values, not code units
    // -----------------------------------------------------------------------

    [Fact]
    public void UpperBound_IncrementsTheLastScalar()
    {
        Assert.Equal("AC", SqlTable.UpperBound("AB"));
    }

    [Fact]
    public void UpperBound_SkipsTheSurrogateRange()
    {
        // Incrementing U+D7FF as a bare code unit produced U+D800 — a lone surrogate, whose UTF-8
        // encoding is undefined for the driver.
        var bound = SqlTable.UpperBound("A퟿");
        Assert.NotNull(bound);
        Assert.Equal("A", bound);
        Assert.DoesNotContain(bound!, c => char.IsSurrogate(c));
    }

    [Fact]
    public void UpperBound_TrailingBmpMaxStillYieldsABound()
    {
        // A prefix ending in U+FFFF used to return null, and the caller then dropped the upper-bound
        // predicate entirely — silently turning a bounded prefix seek into an open-ended scan over
        // the rest of the table, from a caller-supplied search term.
        var bound = SqlTable.UpperBound("A￿");
        Assert.NotNull(bound);
    }

    [Fact]
    public void UpperBound_KeepsSurrogatePairsIntact()
    {
        // "A" + U+1F600 (an emoji). The successor must still be well-formed UTF-16.
        var bound = SqlTable.UpperBound("A\U0001F600");
        Assert.NotNull(bound);
        Assert.True(IsWellFormed(bound!), $"produced ill-formed UTF-16: {bound}");
        Assert.True(string.CompareOrdinal(bound, "A\U0001F600") > 0);
    }

    [Fact]
    public void UpperBound_NullOnlyAtTheTopOfTheSpace()
    {
        Assert.Null(SqlTable.UpperBound("\U0010FFFF"));
    }

    private static bool IsWellFormed(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]))
            {
                if (i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1])) return false;
                i++;
            }
            else if (char.IsLowSurrogate(s[i])) return false;
        }
        return true;
    }

    // -----------------------------------------------------------------------
    // F42 — Postgres TLS
    // -----------------------------------------------------------------------

    [Fact]
    public void Postgres_ConnectionStringWithNoSslMode_IsUpgradedToVerifyFull()
    {
        // Npgsql's default is Prefer: no certificate validation and a silent plaintext fallback. The
        // documented connection string names no mode, so every documented deployment landed there.
        var upgraded = PostgresDialect.RequireVerifiedTls("Host=db;Database=authagonal;Username=auth;Password=x");

        // Asserted on the parsed value, not the rendered text — the spelling is Npgsql's business.
        var parsed = new Npgsql.NpgsqlConnectionStringBuilder(upgraded);
        Assert.Equal(Npgsql.SslMode.VerifyFull, parsed.SslMode);
    }

    [Fact]
    public void Postgres_ExplicitSslMode_IsLeftAlone()
    {
        // An operator who wrote Disable for a local socket meant it. Silence is what gets a default.
        const string explicitDisable = "Host=/var/run/postgresql;Database=authagonal;SSL Mode=Disable";
        Assert.Equal(explicitDisable, PostgresDialect.RequireVerifiedTls(explicitDisable));
    }

    // -----------------------------------------------------------------------
    // F188 — JWKS retention vs signing
    // -----------------------------------------------------------------------

    [Fact]
    public void JwksRetentionGrace_OutlivesAnyTokenTheKeyCouldHaveSigned()
    {
        // A key was dropped from JWKS at exactly ExpiresAt, so every access token signed in the half
        // hour before that was still inside its own exp with a kid no longer published.
        var grace = Authagonal.Protocol.Services.ProtocolSigningKeyOps.JwksRetentionGrace;
        var defaultAccessTokenLifetime = TimeSpan.FromSeconds(new OAuthClient
        {
            ClientId = "x",
            ClientName = "x",
        }.AccessTokenLifetimeSeconds);

        Assert.True(grace > defaultAccessTokenLifetime,
            $"grace {grace} must exceed the default access-token lifetime {defaultAccessTokenLifetime}");
    }

    // -----------------------------------------------------------------------
    // F27 — the raw handle must not reach storage
    // -----------------------------------------------------------------------

    [Fact]
    public void GrantSerializedForStorage_CarriesNoRawHandle()
    {
        // The handle IS the bearer credential — the refresh-token / authorization-code / device-code
        // value — and the whole PersistedGrant was serialized into the data column, so a table dump
        // yielded replayable live tokens. Azure has always blanked it and said why; Dynamo and SQL
        // did not, and the class comment claiming otherwise held only when a field cipher was
        // injected, which is not the OSS or self-hosted default.
        const string handle = "live-refresh-token-handle";
        var stored = Authagonal.SqlProvider.Stores.SqlGrantStore.SerializeWithoutHandle(new PersistedGrant
        {
            Key = handle,
            Type = "refresh_token",
            SubjectId = "user-1",
            ClientId = "client-1",
            Data = "{}",
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
        });

        Assert.DoesNotContain(handle, stored, StringComparison.Ordinal);

        // Everything else must survive — the row is still the grant.
        Assert.Contains("refresh_token", stored, StringComparison.Ordinal);
        Assert.Contains("user-1", stored, StringComparison.Ordinal);
    }

    [Fact]
    public void GrantSerializedForStorage_DoesNotMutateTheCallersObject()
    {
        // Blanked on a copy, so a concurrent reader never observes an empty Key on the live object.
        var grant = new PersistedGrant
        {
            Key = "still-mine",
            Type = "refresh_token",
            ClientId = "client-1",
            Data = "{}",
        };

        Authagonal.SqlProvider.Stores.SqlGrantStore.SerializeWithoutHandle(grant);
        Assert.Equal("still-mine", grant.Key);
    }
}

/// <summary>
/// The seeder must not undo what an operator did through the admin API.
/// </summary>
/// <remarks>
/// ClientSeedService alone built a fresh OAuthClient and Replace-d the row on every pod start, so
/// every property the seed does not state reverted to the model default. Its sibling seeders do the
/// opposite and say so: ScopeSeedService merges field-by-field ("a field omitted from the seed
/// preserves the stored value") and RoleSeedService is documented as "deliberately additive… an
/// operator granting a role through the admin API must not have it taken away by the next restart".
/// </remarks>
public class ClientSeedMergeTests
{
    [Fact]
    public async Task Reseeding_DoesNotReEnableAClientAnOperatorDisabled()
    {
        var store = new InMemoryClientStore();
        await store.UpsertAsync(new OAuthClient
        {
            ClientId = "seeded",
            ClientName = "Seeded",
            Enabled = false,               // disabled through the admin API
            RequireConsent = true,         // hardened through the admin API
            Audiences = ["https://api.example"],
            JwksUri = "https://client.example/jwks",
        });

        await RunSeederAsync(store, clientId: "seeded", clientName: "Seeded");

        var after = await store.GetAsync("seeded");
        Assert.NotNull(after);

        // Enabled is the sharpest of these: the seeder never sets it and the model defaults it to
        // true, so a restart silently re-enabled a client that had been deliberately disabled.
        Assert.False(after!.Enabled);
        Assert.True(after.RequireConsent);
        Assert.Equal(["https://api.example"], after.Audiences);
        Assert.Equal("https://client.example/jwks", after.JwksUri);
    }

    [Fact]
    public async Task Reseeding_PreservesASecretRotatedThroughTheAdminApi()
    {
        var store = new InMemoryClientStore();
        var rotated = CheapHasher.Password().HashPassword("rotated-through-the-admin-api");
        await store.UpsertAsync(new OAuthClient
        {
            ClientId = "seeded",
            ClientName = "Seeded",
            ClientSecretHashes = [rotated],
        });

        // The seed states no secret, so it has nothing to say about this field.
        await RunSeederAsync(store, clientId: "seeded", clientName: "Seeded");

        Assert.Equal([rotated], (await store.GetAsync("seeded"))!.ClientSecretHashes);
    }

    [Fact]
    public async Task Seeding_StillCreatesAClientThatDoesNotExistYet()
    {
        var store = new InMemoryClientStore();
        await RunSeederAsync(store, clientId: "fresh", clientName: "Fresh");

        var created = await store.GetAsync("fresh");
        Assert.NotNull(created);
        Assert.Equal("Fresh", created!.ClientName);
    }

    /// <summary>
    /// AllowedScopes was the one field still assigned unconditionally, from a list that read as empty
    /// when the seed said nothing about scopes — so a seed entry that only pins, say, a redirect URI
    /// stripped every scope an operator had granted through the admin API.
    /// </summary>
    [Fact]
    public async Task Reseeding_DoesNotClearScopesTheSeedSaysNothingAbout()
    {
        var store = new InMemoryClientStore();
        await store.UpsertAsync(new OAuthClient
        {
            ClientId = "seeded",
            AllowedScopes = ["openid", "orders.read"],
        });

        await RunSeederAsync(store, clientId: "seeded", clientName: "Seeded", scope: null);

        Assert.Equal(["openid", "orders.read"], (await store.GetAsync("seeded"))!.AllowedScopes);
    }

    private static async Task RunSeederAsync(
        InMemoryClientStore store, string clientId, string clientName, string? scope = "openid")
    {
        var settings = new Dictionary<string, string?>
        {
            ["Clients:0:ClientId"] = clientId,
            ["Clients:0:ClientName"] = clientName,
        };
        if (scope is not null) settings["Clients:0:AllowedScopes:0"] = scope;

        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        var seeder = new Authagonal.Server.Services.ClientSeedService(
            store,
            CheapHasher.Password(),
            configuration,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Authagonal.Server.Services.ClientSeedService>.Instance);

        await seeder.StartAsync(CancellationToken.None);
    }
}

/// <summary>
/// The same rule, in the other host. <see cref="ClientSeedMergeTests"/> fixed the Server host's
/// <c>ClientSeedService</c> and its sibling in the Protocol host was left building a fresh
/// <c>OAuthClient</c> and upserting it over the stored row on every start.
/// </summary>
/// <remarks>
/// A Protocol-host embedder that configures <c>AuthagonalProtocolOptions.Clients</c> got the whole
/// reported behaviour unchanged: <c>Enabled = true</c> hard-set on every boot, and every field the
/// descriptor has no slot for — PAR, JWKS, CORS, front-channel logout — reset to the model default.
/// </remarks>
public class ProtocolSeedMergeTests
{
    [Fact]
    public async Task Reseeding_DoesNotReEnableAClientAnOperatorDisabled()
    {
        var store = new InMemoryClientStore();
        await store.UpsertAsync(new OAuthClient
        {
            ClientId = "embedded",
            Enabled = false,                                    // disabled through the admin API
            RequirePushedAuthorizationRequests = true,          // hardened through the admin API
            JwksUri = "https://client.example/jwks",            // private_key_jwt for an agent client
            AllowedCorsOrigins = ["https://spa.example"],
            FrontChannelLogoutUri = "https://spa.example/logout",
        });

        await RunSeedAsync(store, new OidcClientDescriptor
        {
            ClientId = "embedded",
            DisplayName = "Embedded",
            RedirectUris = ["https://spa.example/callback"],
        });

        var after = await store.GetAsync("embedded");
        Assert.NotNull(after);

        // The sharpest of these: the seeder set Enabled = true outright, so a restart put a client an
        // operator had deliberately disabled straight back into service.
        Assert.False(after!.Enabled);
        Assert.True(after.RequirePushedAuthorizationRequests);
        Assert.Equal("https://client.example/jwks", after.JwksUri);
        Assert.Equal(["https://spa.example"], after.AllowedCorsOrigins);
        Assert.Equal("https://spa.example/logout", after.FrontChannelLogoutUri);

        // What the descriptor DOES state is still authoritative.
        Assert.Equal("Embedded", after.ClientName);
        Assert.Equal(["https://spa.example/callback"], after.RedirectUris);
    }

    [Fact]
    public async Task Reseeding_PreservesASecretRotatedThroughTheAdminApi()
    {
        var store = new InMemoryClientStore();
        await store.UpsertAsync(new OAuthClient
        {
            ClientId = "embedded",
            ClientSecretHashes = ["$2a$04$rotatedthroughtheadminapiXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX"],
        });

        // The descriptor states no secret, so it has nothing to say about this field.
        await RunSeedAsync(store, new OidcClientDescriptor { ClientId = "embedded" });

        Assert.Equal(
            ["$2a$04$rotatedthroughtheadminapiXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX"],
            (await store.GetAsync("embedded"))!.ClientSecretHashes);
    }

    [Fact]
    public async Task Seeding_StillCreatesAClientThatDoesNotExistYet()
    {
        var store = new InMemoryClientStore();

        await RunSeedAsync(store, new OidcClientDescriptor
        {
            ClientId = "fresh",
            DisplayName = "Fresh",
            AllowRefreshToken = true,
        });

        var created = await store.GetAsync("fresh");
        Assert.NotNull(created);
        Assert.True(created!.Enabled);
        Assert.Equal("Fresh", created.ClientName);
        Assert.Contains("refresh_token", created.AllowedGrantTypes);
        Assert.Equal(RefreshTokenUsage.OneTime, created.RefreshTokenUsage);
    }

    private static async Task RunSeedAsync(InMemoryClientStore store, OidcClientDescriptor descriptor)
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddSingleton<Authagonal.Core.Stores.IClientStore>(store);
        services.AddSingleton<Authagonal.Core.Stores.IScopeStore>(new InMemoryScopeStore());
        await using var provider = services.BuildServiceProvider();

        var seeder = new Authagonal.Protocol.Services.ProtocolSeedService(
            provider.GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
            Microsoft.Extensions.Options.Options.Create(
                new Authagonal.Protocol.AuthagonalProtocolOptions { Clients = [descriptor] }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<
                Authagonal.Protocol.Services.ProtocolSeedService>.Instance);

        await seeder.StartAsync(CancellationToken.None);
    }
}
