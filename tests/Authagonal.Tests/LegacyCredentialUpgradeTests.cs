using Authagonal.Core.Models;
using Authagonal.Core.Stores;
using Authagonal.Protocol.Services;
using Authagonal.Server.Services;
using Authagonal.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Authagonal.Tests;

/// <summary>
/// F240 — a Duende-migrated client secret is stored as a bare unsalted SHA-256/512 digest. The
/// verifier computed the rehash signal and threw it away, so the format lived forever.
/// </summary>
public sealed class LegacyCredentialUpgradeTests
{
    private const string ClientId = "migrated-client";
    private const string Secret = "the-migrated-secret";

    /// <summary>The tag the migration applies, by digest length alone.</summary>
    private static string LegacySha256(string secret) =>
        "SHA256$" + Convert.ToBase64String(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret)));

    private static (PasswordHasher Hasher, IClientStore Store, IServiceProvider Services) Build()
    {
        var hasher = CheapHasher.Password();
        var store = new InMemoryClientStore();
        var services = new ServiceCollection()
            .AddSingleton<IClientStore>(store)
            .BuildServiceProvider();
        return (hasher, store, services);
    }

    [Fact]
    public async Task LegacyDigest_VerifiesAndIsUpgradedInPlace()
    {
        var (hasher, store, services) = Build();
        await store.UpsertAsync(new OAuthClient
        {
            ClientId = ClientId,
            ClientSecretHashes = [LegacySha256(Secret)],
        });

        var verifier = new PasswordHasherClientSecretVerifier(hasher, services);
        var client = (await store.GetAsync(ClientId))!;

        Assert.True(await verifier.VerifyAsync(client, Secret));

        // An unsalted SHA-256 of a client secret is recoverable from a store dump by rainbow table.
        // The plaintext is in hand at exactly the moment of a successful verify, and nowhere else —
        // so that is when the upgrade has to happen, or it never does.
        var upgraded = (await store.GetAsync(ClientId))!;
        var hash = Assert.Single(upgraded.ClientSecretHashes);
        Assert.False(PasswordHasher.IsUnsaltedDigestHash(hash), $"still on the legacy format: {hash}");

        // …and the upgraded hash still authenticates, which is the part that would take the
        // deployment down if it were wrong.
        Assert.True(await verifier.VerifyAsync(upgraded, Secret));
        Assert.False(await verifier.VerifyAsync(upgraded, "wrong-secret"));
    }

    [Fact]
    public async Task WrongSecret_DoesNotTouchTheStoredHash()
    {
        var (hasher, store, services) = Build();
        var legacy = LegacySha256(Secret);
        await store.UpsertAsync(new OAuthClient { ClientId = ClientId, ClientSecretHashes = [legacy] });

        var verifier = new PasswordHasherClientSecretVerifier(hasher, services);
        Assert.False(await verifier.VerifyAsync((await store.GetAsync(ClientId))!, "wrong-secret"));

        Assert.Equal(legacy, Assert.Single((await store.GetAsync(ClientId))!.ClientSecretHashes));
    }

    [Fact]
    public async Task UpgradeFailure_DoesNotFailAuthentication()
    {
        // Authentication has already succeeded by the time the upgrade runs. A store that refuses the
        // write must not turn a valid credential into a rejected one — it just means the next call
        // tries again.
        var hasher = CheapHasher.Password();
        var services = new ServiceCollection()
            .AddSingleton<IClientStore>(new ThrowingClientStore())
            .BuildServiceProvider();

        var verifier = new PasswordHasherClientSecretVerifier(hasher, services);
        var client = new OAuthClient { ClientId = ClientId, ClientSecretHashes = [LegacySha256(Secret)] };

        Assert.True(await verifier.VerifyAsync(client, Secret));
    }

    /// <summary>
    /// A SCOPED <c>IClientStore</c> — how every multi-tenant host registers it — still upgrades, and above all
    /// does not fault the authentication.
    /// </summary>
    /// <remarks>
    /// The verifier is registered <c>TryAddSingleton</c>, so the provider it holds is the ROOT provider, and
    /// <c>GetService&lt;IClientStore&gt;()</c> against the root THROWS for a scoped registration. That
    /// resolution sat outside the try/catch, so the exception escaped <c>VerifyAsync</c> after the presented
    /// secret had already verified: correct <c>client_credentials</c> answered 500 instead of a token, on
    /// <c>/connect/token</c>, <c>/par</c>, <c>/introspect</c>, <c>/revocation</c> and
    /// <c>/deviceauthorization</c> — permanently, because the upgrade could never complete, and triggered by
    /// the legitimate credential rather than by an attacker.
    /// <para>
    /// Nothing in the suite registered the store as scoped, which is precisely why it shipped: every existing
    /// test here uses <c>AddSingleton</c>, where root resolution happens to work. This is the same defect
    /// <c>LegacySecretHashWarning</c> had — it stopped tenant-scoped hosts from starting at all until it
    /// resolved inside a scope — reintroduced one file over.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AScopedClientStore_IsReachedThroughAScope_AndDoesNotFaultAuthentication()
    {
        var hasher = CheapHasher.Password();
        var store = new InMemoryClientStore();
        await store.UpsertAsync(new OAuthClient
        {
            ClientId = ClientId,
            ClientSecretHashes = [LegacySha256(Secret)],
        });

        // Scoped, and with scope validation on — exactly what a tenant-scoped host does.
        var services = new ServiceCollection()
            .AddScoped<IClientStore>(_ => store)
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        var verifier = new PasswordHasherClientSecretVerifier(hasher, services);
        var client = (await store.GetAsync(ClientId))!;

        Assert.True(await verifier.VerifyAsync(client, Secret));

        // And the upgrade actually reached the store, rather than being swallowed as a failed resolution.
        var stored = Assert.Single((await store.GetAsync(ClientId))!.ClientSecretHashes);
        Assert.False(PasswordHasher.IsUnsaltedDigestHash(stored), $"still on the legacy format: {stored}");
    }

    [Fact]
    public async Task WithNoClientStore_StillVerifies()
    {
        // A Protocol-only host that never registers IClientStore into the same container keeps
        // working, unupgraded.
        var verifier = new PasswordHasherClientSecretVerifier(CheapHasher.Password());
        var client = new OAuthClient { ClientId = ClientId, ClientSecretHashes = [LegacySha256(Secret)] };

        Assert.True(await verifier.VerifyAsync(client, Secret));
    }

    [Fact]
    public void UnsaltedDigestDetection_NamesOnlyTheWeakFormats()
    {
        Assert.True(PasswordHasher.IsUnsaltedDigestHash(LegacySha256(Secret)));
        Assert.True(PasswordHasher.IsUnsaltedDigestHash("SHA512$abc"));
        Assert.False(PasswordHasher.IsUnsaltedDigestHash(CheapHasher.Password().HashPassword(Secret)));
        Assert.False(PasswordHasher.IsUnsaltedDigestHash(""));
    }

    // -----------------------------------------------------------------------
    // Doubles
    // -----------------------------------------------------------------------

    private sealed class InMemoryClientStore : IClientStore
    {
        private readonly Dictionary<string, OAuthClient> _clients = new(StringComparer.Ordinal);

        public Task<OAuthClient?> GetAsync(string clientId, CancellationToken ct = default) =>
            // A copy, so the verifier cannot "upgrade" by mutating the store's own instance.
            Task.FromResult(_clients.TryGetValue(clientId, out var c)
                ? c with { ClientSecretHashes = [.. c.ClientSecretHashes] }
                : null);

        public Task<IReadOnlyList<OAuthClient>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<OAuthClient>>([.. _clients.Values]);

        public Task UpsertAsync(OAuthClient client, CancellationToken ct = default)
        {
            _clients[client.ClientId] = client with { ClientSecretHashes = [.. client.ClientSecretHashes] };
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string clientId, CancellationToken ct = default)
        {
            _clients.Remove(clientId);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Compare-and-set on the one entry, still detached — the double's whole point is that the verifier
        /// cannot "upgrade" by mutating an instance it already holds.
        /// </summary>
        public Task<bool> TryUpgradeSecretHashAsync(
            string clientId, int index, string expectedHash, string newHash, CancellationToken ct = default)
        {
            if (!_clients.TryGetValue(clientId, out var c)) return Task.FromResult(false);
            if (index >= c.ClientSecretHashes.Count) return Task.FromResult(false);
            if (!string.Equals(c.ClientSecretHashes[index], expectedHash, StringComparison.Ordinal))
                return Task.FromResult(false);

            var upgraded = new List<string>(c.ClientSecretHashes) { [index] = newHash };
            _clients[clientId] = c with { ClientSecretHashes = upgraded };
            return Task.FromResult(true);
        }
    }

    private sealed class ThrowingClientStore : IClientStore
    {
        public Task<OAuthClient?> GetAsync(string clientId, CancellationToken ct = default) =>
            throw new InvalidOperationException("store unavailable");

        public Task<IReadOnlyList<OAuthClient>> GetAllAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("store unavailable");

        public Task UpsertAsync(OAuthClient client, CancellationToken ct = default) =>
            throw new InvalidOperationException("store unavailable");

        public Task DeleteAsync(string clientId, CancellationToken ct = default) =>
            throw new InvalidOperationException("store unavailable");

        /// <summary>
        /// Throws too, so the "a failed upgrade must not reject a valid credential" test still exercises the
        /// catch. Left on the interface default it would return false instead, and the test would pass because
        /// nothing was attempted rather than because the failure was handled.
        /// </summary>
        public Task<bool> TryUpgradeSecretHashAsync(
            string clientId, int index, string expectedHash, string newHash, CancellationToken ct = default) =>
            throw new InvalidOperationException("store unavailable");
    }
}
