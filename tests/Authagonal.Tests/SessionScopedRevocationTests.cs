using Authagonal.AzureProvider.Stores;
using Authagonal.Core.Constants;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Protocol;
using Authagonal.Protocol.Models;
using Authagonal.Protocol.Services;
using Authagonal.Tests.Infrastructure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace Authagonal.Tests;

/// <summary>
/// Ending ONE sign-in session has to end that session's tokens — and only that session's.
/// </summary>
/// <remarks>
/// <c>POST /api/auth/sessions/{id}/revoke</c> and <c>POST /api/auth/sessions/revoke-others</c> deleted the
/// <c>Sessions</c> row and nothing else. The refresh token the relying party on that device already held was a
/// <c>refresh_token</c> grant nothing touched, so <c>POST /connect/token</c> from it kept succeeding and
/// rotating for the client's whole absolute refresh lifetime: a user who lost a laptop and clicked "Log out
/// other devices" from their phone was told every other device was signed out while the thief retained RP
/// access. No relying party was notified either, so each went on believing the user was present.
/// <para>
/// It was inexpressible rather than forgotten. <c>IGrantStore</c> removed by subject, or by subject and client,
/// and a grant carried no session identity — so the only available calls were "leave the tokens alive" or "kill
/// every session's tokens", and the second signs the user out of the device they deliberately kept.
/// <see cref="PersistedGrant.SessionId"/> and <c>RemoveBySessionAsync</c> exist for this.
/// </para>
/// <para>
/// Against Azurite for the store half, because the selection happens over real index rows. SQL and DynamoDB
/// share one <c>RemoveBySubjectCoreAsync</c> with the same session predicate, so they are covered structurally
/// rather than by a container round trip here.
/// </para>
/// </remarks>
[Collection("Azurite")]
public class SessionScopedRevocationTests(AzuriteFixture azurite)
{
    private readonly TableServiceClient _svc = new(azurite.ConnectionString);

    private TableGrantStore NewStore(string prefix)
    {
        TableClient T(string name)
        {
            var c = _svc.GetTableClient($"{prefix}{name}");
            c.CreateIfNotExists();
            return c;
        }

        return new TableGrantStore(
            T("Grants"), T("GrantsBySubject"), T("GrantsByExpiry"), EnvPartitioner.Live,
            NullLogger<TableGrantStore>.Instance, fieldCipher: null);
    }

    private static string Prefix() => $"sessrev{Guid.NewGuid():N}"[..20];

    private static PersistedGrant Refresh(string handle, string subject, string? sessionId) => new()
    {
        Key = handle,
        Type = PersistedGrantTypes.RefreshToken,
        SubjectId = subject,
        ClientId = "web",
        SessionId = sessionId,
        Data = "{}",
        CreatedAt = DateTimeOffset.UtcNow,
        ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
    };

    [Fact]
    public async Task RevokingOneSession_TakesItsGrant_AndLeavesTheOthers()
    {
        var store = NewStore(Prefix());
        await store.StoreAsync(Refresh("laptop-handle", "user-1", "sid-laptop"));
        await store.StoreAsync(Refresh("phone-handle", "user-1", "sid-phone"));

        var removed = await store.RemoveBySessionAsync(
            "user-1", PersistedGrantTypes.SessionBound, "sid-laptop");

        Assert.Equal(1, removed);
        Assert.Null(await store.GetAsync("laptop-handle"));
        Assert.NotNull(await store.GetAsync("phone-handle"));
    }

    /// <summary>"Log out other devices": every session except the caller's.</summary>
    [Fact]
    public async Task RevokingOtherSessions_KeepsTheCallersGrant_AndTakesTheRest()
    {
        var store = NewStore(Prefix());
        await store.StoreAsync(Refresh("phone-handle", "user-1", "sid-phone"));
        await store.StoreAsync(Refresh("laptop-handle", "user-1", "sid-laptop"));
        await store.StoreAsync(Refresh("tablet-handle", "user-1", "sid-tablet"));

        var removed = await store.RemoveBySessionAsync(
            "user-1", PersistedGrantTypes.SessionBound, "sid-phone", invert: true);

        Assert.Equal(2, removed);
        Assert.NotNull(await store.GetAsync("phone-handle"));
        Assert.Null(await store.GetAsync("laptop-handle"));
        Assert.Null(await store.GetAsync("tablet-handle"));
    }

    /// <summary>
    /// A grant with no session identity is never taken, in either direction.
    /// </summary>
    /// <remarks>
    /// It cannot be attributed to the session being ended, so ending that session must not destroy it. This is
    /// also what makes the call safe for grants written before the field existed, and for
    /// <c>client_credentials</c> and token-exchange grants, which have no session at all.
    /// </remarks>
    [Fact]
    public async Task AGrantWithNoSessionId_IsNeverTaken_InEitherDirection()
    {
        var store = NewStore(Prefix());
        await store.StoreAsync(Refresh("service-handle", "user-1", sessionId: null));
        await store.StoreAsync(Refresh("laptop-handle", "user-1", "sid-laptop"));

        Assert.Equal(1, await store.RemoveBySessionAsync(
            "user-1", PersistedGrantTypes.SessionBound, "sid-laptop"));
        Assert.NotNull(await store.GetAsync("service-handle"));

        // And the inverted direction, which is the one that would otherwise sweep it up.
        Assert.Equal(0, await store.RemoveBySessionAsync(
            "user-1", PersistedGrantTypes.SessionBound, "sid-phone", invert: true));
        Assert.NotNull(await store.GetAsync("service-handle"));
    }

    [Fact]
    public async Task ASessionIdThatMatchesNothing_RemovesNothing()
    {
        var store = NewStore(Prefix());
        await store.StoreAsync(Refresh("laptop-handle", "user-1", "sid-laptop"));

        Assert.Equal(0, await store.RemoveBySessionAsync(
            "user-1", PersistedGrantTypes.SessionBound, "sid-unknown"));
        Assert.NotNull(await store.GetAsync("laptop-handle"));
    }

    /// <summary>
    /// The access tokens the ended session's refresh grant minted are revoked too — and the kept session's
    /// are not.
    /// </summary>
    /// <remarks>
    /// The same both-halves rule <c>GrantRevocation</c> exists to enforce: an access token is a self-contained
    /// ES256 JWT, so removing its refresh grant leaves it valid to its own <c>exp</c>. Ending a session that
    /// leaves the device's access token working for another half hour has not ended it.
    /// </remarks>
    [Fact]
    public async Task RevokingASession_AlsoRevokesThatSessionsTrackedAccessTokens()
    {
        var grants = new InMemoryGrantStore();
        var revoked = new InMemoryRevokedTokenStore();
        var expiry = DateTimeOffset.UtcNow.AddMinutes(30);

        await grants.StoreAsync(WithTrackedToken("laptop-handle", "sid-laptop", "jti-laptop", expiry));
        await grants.StoreAsync(WithTrackedToken("phone-handle", "sid-phone", "jti-phone", expiry));

        var count = await GrantRevocation.RevokeSessionGrantsAsync(
            grants, revoked, "user-1", "sid-laptop");

        Assert.Equal(1, count);
        Assert.True(await revoked.IsRevokedAsync("jti-laptop"));
        Assert.False(await revoked.IsRevokedAsync("jti-phone"));
        Assert.Null(await grants.GetAsync("laptop-handle"));
        Assert.NotNull(await grants.GetAsync("phone-handle"));
    }

    private static PersistedGrant WithTrackedToken(
        string handle, string sessionId, string jti, DateTimeOffset expiresAt)
    {
        var grant = Refresh(handle, "user-1", sessionId);
        grant.Data = JsonSerializer.Serialize(new RefreshTokenData
        {
            SubjectId = "user-1",
            ClientId = "web",
            Scopes = ["openid"],
            CreatedAt = DateTimeOffset.UtcNow,
            Subject = new OidcSubject { SubjectId = "user-1" },
            AccessTokens = [new IssuedAccessToken { Jti = jti, ExpiresAt = expiresAt }],
        }, ProtocolJsonContext.Default.RefreshTokenData);
        return grant;
    }
}
