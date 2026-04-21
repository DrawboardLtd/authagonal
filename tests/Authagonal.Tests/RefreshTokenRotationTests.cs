using System.Security.Cryptography;
using System.Text;
using Authagonal.Core.Stores;
using Authagonal.Protocol;
using Authagonal.Protocol.Services;
using Authagonal.Server.Services;
using Authagonal.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Authagonal.Tests;

/// <summary>
/// Unit-style tests for refresh token rotation semantics:
///   * strict default (reuse revokes family)
///   * opt-in grace window (retry within window is idempotent)
///   * session cap clamping and preservation across rotations
/// These drive <see cref="IProtocolTokenService"/> directly so we can exercise behaviour
/// that would be awkward to express via HTTP (e.g. mutating AuthOptions or inspecting
/// grant state).
/// </summary>
public sealed class RefreshTokenRotationTests
{
    private const string ClientId = AuthagonalTestFactory.TestClientId;

    [Fact]
    public async Task StrictDefault_ReuseOfConsumedToken_RevokesFamily()
    {
        await using var factory = new AuthagonalTestFactory();
        await factory.SeedTestDataAsync();
        var user = await factory.SeedTestUserAsync();

        using var scope = factory.Services.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<IProtocolTokenService>();
        var resolver = scope.ServiceProvider.GetRequiredService<UserStoreOidcSubjectResolver>();
        var client = (await factory.Services.GetRequiredService<IClientStore>().GetAsync(ClientId))!;
        var subject = await resolver.BuildSubjectAsync(user, client);

        var handle = await tokens.CreateRefreshTokenAsync(subject, client, ["openid", "offline_access"]);

        // First rotation succeeds.
        var first = await tokens.HandleRefreshTokenAsync(handle, ClientId);
        Assert.NotNull(first.RefreshToken);
        Assert.NotEqual(handle, first.RefreshToken);

        // Replay of the original consumed handle — with default (grace = 0) — revokes
        // all grants for this subject+client.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            tokens.HandleRefreshTokenAsync(handle, ClientId));

        // The successor must have been swept up by the revoke policy.
        Assert.Null(await factory.GrantStore.GetAsync(first.RefreshToken!));
    }

    [Fact]
    public async Task GraceWindow_RetryWithinWindow_ReturnsFreshTokensAnchoredToSuccessor()
    {
        await using var factory = new AuthagonalTestFactory
        {
            ConfigureAuthOptions = o => o.RefreshTokenReuseGraceSeconds = 30
        };
        await factory.SeedTestDataAsync();
        var user = await factory.SeedTestUserAsync();

        using var scope = factory.Services.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<IProtocolTokenService>();
        var resolver = scope.ServiceProvider.GetRequiredService<UserStoreOidcSubjectResolver>();
        var client = (await factory.Services.GetRequiredService<IClientStore>().GetAsync(ClientId))!;
        var subject = await resolver.BuildSubjectAsync(user, client);

        var handle = await tokens.CreateRefreshTokenAsync(subject, client, ["openid", "offline_access"]);

        // First refresh rotates cleanly.
        var first = await tokens.HandleRefreshTokenAsync(handle, ClientId);

        // Retry with the original handle: grace window should treat this as idempotent,
        // re-delivering the *same* successor handle with fresh access/id tokens.
        var retry = await tokens.HandleRefreshTokenAsync(handle, ClientId);

        Assert.Equal(first.RefreshToken, retry.RefreshToken);
        Assert.NotNull(retry.AccessToken);
        Assert.NotEqual(first.AccessToken, retry.AccessToken);
        Assert.NotNull(await factory.GrantStore.GetAsync(first.RefreshToken!));
    }

    [Fact]
    public async Task GraceWindow_RetryAfterSuccessorConsumed_RevokesFamily()
    {
        // Once the successor has itself been rotated, the original handle no longer has
        // a "fresh" successor to replay — reuse reverts to replay-revoke regardless of
        // whether the clock is still inside the window.
        await using var factory = new AuthagonalTestFactory
        {
            ConfigureAuthOptions = o => o.RefreshTokenReuseGraceSeconds = 30
        };
        await factory.SeedTestDataAsync();
        var user = await factory.SeedTestUserAsync();

        using var scope = factory.Services.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<IProtocolTokenService>();
        var resolver = scope.ServiceProvider.GetRequiredService<UserStoreOidcSubjectResolver>();
        var client = (await factory.Services.GetRequiredService<IClientStore>().GetAsync(ClientId))!;
        var subject = await resolver.BuildSubjectAsync(user, client);

        var handle = await tokens.CreateRefreshTokenAsync(subject, client, ["openid", "offline_access"]);

        var first = await tokens.HandleRefreshTokenAsync(handle, ClientId);
        var second = await tokens.HandleRefreshTokenAsync(first.RefreshToken!, ClientId);

        // Replay of the original handle: its successor (first.RefreshToken) has been
        // consumed by the second rotation, so grace window cannot idempotently replay.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            tokens.HandleRefreshTokenAsync(handle, ClientId));

        // Entire family swept.
        Assert.Null(await factory.GrantStore.GetAsync(first.RefreshToken!));
        Assert.Null(await factory.GrantStore.GetAsync(second.RefreshToken!));
    }

    [Fact]
    public async Task SessionCap_AtIssuance_ClampsRefreshExpiry()
    {
        await using var factory = new AuthagonalTestFactory();
        await factory.SeedTestDataAsync();
        var user = await factory.SeedTestUserAsync();

        using var scope = factory.Services.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<IProtocolTokenService>();
        var resolver = scope.ServiceProvider.GetRequiredService<UserStoreOidcSubjectResolver>();
        var client = (await factory.Services.GetRequiredService<IClientStore>().GetAsync(ClientId))!;

        // Cap the session at 10 minutes from now — well below the default refresh lifetime.
        var cap = DateTimeOffset.UtcNow.AddMinutes(10);
        var subject = await resolver.BuildSubjectAsync(user, client, sessionMaxExpiresAt: cap);
        var handle = await tokens.CreateRefreshTokenAsync(subject, client, ["openid", "offline_access"]);

        var grant = await factory.GrantStore.GetAsync(handle);
        Assert.NotNull(grant);
        // Allow for a tiny bit of skew from clock reads inside CreateRefreshTokenAsync.
        Assert.True(grant.ExpiresAt <= cap.AddSeconds(1));
        Assert.True(grant.ExpiresAt >= cap.AddSeconds(-2));
    }

    [Fact]
    public async Task SessionCap_SurvivesRotation_CannotBeLiftedByRefreshing()
    {
        await using var factory = new AuthagonalTestFactory();
        await factory.SeedTestDataAsync();
        var user = await factory.SeedTestUserAsync();

        using var scope = factory.Services.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<IProtocolTokenService>();
        var resolver = scope.ServiceProvider.GetRequiredService<UserStoreOidcSubjectResolver>();
        var client = (await factory.Services.GetRequiredService<IClientStore>().GetAsync(ClientId))!;

        var cap = DateTimeOffset.UtcNow.AddMinutes(10);
        var subject = await resolver.BuildSubjectAsync(user, client, sessionMaxExpiresAt: cap);
        var handle = await tokens.CreateRefreshTokenAsync(subject, client, ["openid", "offline_access"]);

        var rotated = await tokens.HandleRefreshTokenAsync(handle, ClientId);

        var successor = await factory.GrantStore.GetAsync(rotated.RefreshToken!);
        Assert.NotNull(successor);
        // The rotated grant must carry the same ceiling — refresh cannot push expiry past
        // the federation-derived cap, no matter how many rotations occur.
        Assert.True(successor.ExpiresAt <= cap.AddSeconds(1));
    }

    [Fact]
    public async Task SessionCap_FromAuthorizationCode_PropagatesToRefresh()
    {
        await using var factory = new AuthagonalTestFactory();
        await factory.SeedTestDataAsync();
        var user = await factory.SeedTestUserAsync();

        using var scope = factory.Services.CreateScope();
        var authCode = scope.ServiceProvider.GetRequiredService<ProtocolAuthorizationCodeService>();
        var tokens = scope.ServiceProvider.GetRequiredService<IProtocolTokenService>();
        var resolver = scope.ServiceProvider.GetRequiredService<UserStoreOidcSubjectResolver>();
        var client = (await factory.Services.GetRequiredService<IClientStore>().GetAsync(ClientId))!;

        var (verifier, challenge) = GeneratePkce();
        var cap = DateTimeOffset.UtcNow.AddMinutes(10);
        var subject = await resolver.BuildSubjectAsync(user, client, sessionMaxExpiresAt: cap);
        var code = await authCode.CreateCodeAsync(
            clientId: ClientId,
            subject: subject,
            redirectUri: "https://app.test/callback",
            scopes: ["openid", "offline_access"],
            codeChallenge: challenge,
            codeChallengeMethod: "S256",
            nonce: null,
            resources: null);

        var response = await tokens.HandleAuthorizationCodeAsync(
            code, ClientId, "https://app.test/callback", codeVerifier: verifier);

        Assert.NotNull(response.RefreshToken);
        var refreshGrant = await factory.GrantStore.GetAsync(response.RefreshToken!);
        Assert.NotNull(refreshGrant);
        Assert.True(refreshGrant.ExpiresAt <= cap.AddSeconds(1));
    }

    private static (string Verifier, string Challenge) GeneratePkce()
    {
        var verifierBytes = RandomNumberGenerator.GetBytes(32);
        var verifier = Convert.ToBase64String(verifierBytes)
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var challengeBytes = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        var challenge = Convert.ToBase64String(challengeBytes)
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        return (verifier, challenge);
    }
}
