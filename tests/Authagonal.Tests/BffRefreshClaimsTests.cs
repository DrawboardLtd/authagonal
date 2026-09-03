using System.Collections.Concurrent;
using System.Security.Claims;
using Authagonal.Bff;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Authagonal.Tests;

/// <summary>A session's claims follow the refreshed id_token. A role granted after login used to
/// stay invisible for the session's whole life (30 days, persistent cookie): the refresh swapped in
/// the new id_token and never read it.</summary>
public class BffRefreshClaimsTests
{
    [Fact]
    public async Task A_refresh_re_reads_the_sessions_claims_from_the_new_id_token()
    {
        var store = new MemoryStore();
        var session = NearExpiry("user-1", new() { ["roles"] = "ca:admin", ["email"] = "sam@example" });
        await store.SetAsync(session);
        var coordinator = Build(store, new Tokens(IdToken("user-1", ["ca:admin", "ca:tier2"])), new ParsingReader());

        var fresh = await coordinator.EnsureFreshAsync(session);

        Assert.NotNull(fresh);
        Assert.Equal("ca:admin ca:tier2", fresh!.Claims["roles"]);
        Assert.Equal("sam@example", fresh.Claims["email"]);
        Assert.Equal("ca:admin ca:tier2", (await store.GetAsync(session.SessionId))!.Claims["roles"]);
    }

    [Fact]
    public async Task A_token_for_another_subject_or_an_invalid_one_leaves_the_claims_alone()
    {
        var store = new MemoryStore();
        var session = NearExpiry("user-1", new() { ["roles"] = "ca:admin" });
        await store.SetAsync(session);
        var other = await Build(store, new Tokens(IdToken("user-2", ["ca:tier2"])), new ParsingReader()).EnsureFreshAsync(session);
        Assert.Equal("ca:admin", other!.Claims["roles"]);

        var session2 = NearExpiry("user-1", new() { ["roles"] = "ca:admin" });
        await store.SetAsync(session2);
        var invalid = await Build(store, new Tokens(IdToken("user-1", ["ca:tier2"])), new RefusingReader()).EnsureFreshAsync(session2);
        Assert.Equal("ca:admin", invalid!.Claims["roles"]);
    }

    static BffRefreshCoordinator Build(IBffSessionStore store, ITokenClient tokens, IBffIdTokenReader reader) =>
        new(tokens, store, new OneTenant(), Options.Create(new AuthagonalBffOptions
        {
            Authority = "https://idp.example", ClientId = "bff", ClientSecret = "secret", RefreshThresholdSeconds = 60,
        }), NullLogger<BffRefreshCoordinator>.Instance, null, reader);

    static BffSession NearExpiry(string subject, Dictionary<string, string> claims) => new()
    {
        SessionId = Guid.NewGuid().ToString("N"), Subject = subject, IdToken = "id-0", AccessToken = "access-0",
        RefreshToken = "refresh-0", AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(30),
        ExpiresAt = DateTimeOffset.UtcNow.AddHours(8), CreatedAt = DateTimeOffset.UtcNow, Claims = claims,
    };

    /// <summary>An unsigned id_token whose claims are the only thing under test — the reader
    /// fakes stand in for signature validation, which the login path already pins elsewhere.</summary>
    static string IdToken(string subject, string[] roles) => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
    {
        Issuer = "https://idp.example", Audience = "bff", Expires = DateTime.UtcNow.AddHours(1),
        Claims = new Dictionary<string, object> { ["sub"] = subject, ["email"] = "sam@example", ["roles"] = roles },
    });

    sealed class ParsingReader : IBffIdTokenReader
    {
        public Task<JsonWebToken?> TryReadAsync(BffTenantConfig tenant, string idToken, CancellationToken ct = default)
            => Task.FromResult<JsonWebToken?>(new JsonWebToken(idToken));
    }

    sealed class RefusingReader : IBffIdTokenReader
    {
        public Task<JsonWebToken?> TryReadAsync(BffTenantConfig tenant, string idToken, CancellationToken ct = default)
            => Task.FromResult<JsonWebToken?>(null);
    }

    sealed class Tokens(string idToken) : ITokenClient
    {
        public Task<TokenResult> RefreshAsync(BffTenantConfig tenant, string refreshToken, CancellationToken ct = default)
            => Task.FromResult(new TokenResult("access-1", "refresh-1", idToken, 3600));
        public Task<TokenResult> ExchangeCodeAsync(BffTenantConfig tenant, string code, string redirectUri, string codeVerifier, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task RevokeAsync(BffTenantConfig tenant, string refreshToken, CancellationToken ct = default) => Task.CompletedTask;
        public Task<TokenResult> ExchangeTokenAsync(BffTenantConfig tenant, string subjectToken, IReadOnlyDictionary<string, string>? extraParameters = null, string? scope = null, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    sealed class OneTenant : IBffTenantResolver
    {
        static readonly BffTenantConfig Config = new()
            { Authority = "https://idp.example", ClientId = "bff", ClientSecret = "secret", Scope = ["openid"] };
        public Task<BffTenantConfig?> ResolveAsync(string? tenantKey, CancellationToken ct = default) => Task.FromResult<BffTenantConfig?>(Config);
        public Task<BffTenantConfig?> ResolveByIssuerAsync(string issuer, CancellationToken ct = default) => Task.FromResult<BffTenantConfig?>(Config);
    }

    sealed class MemoryStore : IBffSessionStore
    {
        readonly ConcurrentDictionary<string, BffSession> _sessions = new();
        public Task<BffSession?> GetAsync(string sessionId, CancellationToken ct = default)
            => Task.FromResult(_sessions.TryGetValue(sessionId, out var s) ? s : null);
        public Task SetAsync(BffSession session, CancellationToken ct = default) { _sessions[session.SessionId] = session; return Task.CompletedTask; }
        public Task RemoveAsync(string sessionId, CancellationToken ct = default) { _sessions.TryRemove(sessionId, out _); return Task.CompletedTask; }
        public Task<int> RemoveBySidAsync(string sid, string? tenantKey = null, CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> RemoveBySubjectAsync(string subject, string? tenantKey = null, CancellationToken ct = default) => Task.FromResult(0);
    }
}
