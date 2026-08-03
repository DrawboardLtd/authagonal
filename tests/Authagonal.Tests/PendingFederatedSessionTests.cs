using System.Security.Claims;
using Authagonal.Server.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

/// <summary>
/// A federated login parked on an MFA challenge must resume as the SAME session, not a bare one.
/// </summary>
/// <remarks>
/// When a federated user has MFA enrolled, <c>FederatedMfaFlow.MaybeChallengeAsync</c> returns a redirect — so
/// the callback returns BEFORE it builds its sign-in principal, and every federation binding was added only on
/// the fall-through path below that point. The session was then established by <c>/api/auth/mfa/verify</c> via
/// <c>CookieSignInHelper.SignInAsync</c>, which minted only <c>sub</c>, <c>email</c>, <c>name</c>,
/// <c>security_stamp</c>, a FRESH <c>sid</c>, <c>auth_time</c>, <c>org_id</c> and <c>mfa_authenticated</c>.
/// <para>
/// So for exactly the federated users who had MFA: <b>single logout stopped working</b> (SLO matches a session
/// by <c>saml_name_id</c>, which was gone — enabling MFA on a federated tenant quietly disabled SLO), the IdP's
/// session bound was discarded so the local session outlived the authentication behind it, the upstream refresh
/// token was stranded under an <c>sid</c> nothing would look up again, and <c>federated:*</c> claims vanished
/// from tokens.
/// </para>
/// <para>
/// Nothing in the suite exercised the federated-MFA path, which is why it shipped. These pin the carrier and
/// the merge; the redirect-to-verify round trip is covered only by the wiring's types.
/// </para>
/// </remarks>
public sealed class PendingFederatedSessionTests
{
    private const string Subject = "user-1";
    private const string ChallengeId = "chal-1";

    private static Claim[] FederationClaims() =>
    [
        new("saml_connection", "acme-conn"),
        new("saml_name_id", "ceo@acme.com"),
        new("saml_session_index", "idx-9"),
        new("session_max_exp", "1800000000"),
        new("federated:department", "Research"),
    ];

    [Fact]
    public async Task TheParkedBindingsSurviveTheRoundTrip()
    {
        var grants = new InMemoryGrantStore();
        var bound = DateTimeOffset.UtcNow.AddHours(8);

        await PendingFederatedSession.StoreAsync(
            grants, ChallengeId, Subject, "client-1", FederationClaims(), bound,
            DateTimeOffset.UtcNow.AddMinutes(5), default, sessionId: "sid-from-callback");

        var parked = await PendingFederatedSession.ConsumeAsync(grants, ChallengeId, Subject);

        Assert.NotNull(parked);
        var claims = parked!.ToClaims().ToDictionary(c => c.Type, c => c.Value, StringComparer.Ordinal);

        // The SLO subject is the one that mattered most: without it an IdP-initiated logout cannot find
        // this session at all.
        Assert.Equal("ceo@acme.com", claims["saml_name_id"]);
        Assert.Equal("acme-conn", claims["saml_connection"]);
        Assert.Equal("idx-9", claims["saml_session_index"]);
        Assert.Equal("1800000000", claims["session_max_exp"]);
        Assert.Equal("Research", claims["federated:department"]);

        // The sid the callback committed to — the upstream refresh token is stored under it.
        Assert.Equal("sid-from-callback", parked.SessionId);

        // And the IdP's cookie bound, to the second.
        Assert.Equal(bound.ToUnixTimeSeconds(), parked.CookieExpires!.Value.ToUnixTimeSeconds());
    }

    /// <summary>
    /// Single-use: the challenge it belongs to is single-use, so the parked state must not outlive it.
    /// </summary>
    [Fact]
    public async Task TheParkedStateIsConsumedOnce()
    {
        var grants = new InMemoryGrantStore();
        await PendingFederatedSession.StoreAsync(
            grants, ChallengeId, Subject, null, FederationClaims(), null,
            DateTimeOffset.UtcNow.AddMinutes(5));

        Assert.NotNull(await PendingFederatedSession.ConsumeAsync(grants, ChallengeId, Subject));
        Assert.Null(await PendingFederatedSession.ConsumeAsync(grants, ChallengeId, Subject));
    }

    /// <summary>
    /// A challenge id whose parked state belongs to someone else yields nothing.
    /// </summary>
    /// <remarks>
    /// The parked state is what a federated cookie gets signed from, so binding it to the subject the challenge
    /// was issued for is the difference between resuming a session and assembling one out of two logins.
    /// </remarks>
    [Fact]
    public async Task ParkedStateForAnotherSubjectIsRefused()
    {
        var grants = new InMemoryGrantStore();
        await PendingFederatedSession.StoreAsync(
            grants, ChallengeId, Subject, null, FederationClaims(), null,
            DateTimeOffset.UtcNow.AddMinutes(5));

        Assert.Null(await PendingFederatedSession.ConsumeAsync(grants, ChallengeId, "someone-else"));
    }

    /// <summary>
    /// Expired parked state is not usable, even though the row is still there.
    /// </summary>
    [Fact]
    public async Task ExpiredParkedStateIsRefused()
    {
        var grants = new InMemoryGrantStore();
        await PendingFederatedSession.StoreAsync(
            grants, ChallengeId, Subject, null, FederationClaims(), null,
            DateTimeOffset.UtcNow.AddMinutes(-1));

        Assert.Null(await PendingFederatedSession.ConsumeAsync(grants, ChallengeId, Subject));
    }

    /// <summary>
    /// The control: a password login parks nothing, and the verify path must be unchanged for it.
    /// </summary>
    /// <remarks>
    /// Null is the ordinary case. Without this assertion a carrier that threw or invented defaults on absence
    /// would break every non-federated MFA sign-in — the overwhelming majority of them.
    /// </remarks>
    [Fact]
    public async Task NothingParkedYieldsNull()
        => Assert.Null(await PendingFederatedSession.ConsumeAsync(
            new InMemoryGrantStore(), "never-parked", Subject));
    // ── the sign-in merge ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The parked bindings actually reach the cookie, and the parked <c>sid</c> is the one used.
    /// </summary>
    /// <remarks>
    /// Asserted against <c>CookieSignInHelper.SignInAsync</c> itself rather than only the carrier, because the
    /// carrier round-tripping proves nothing about whether the sign-in uses it — the first version of these
    /// tests passed with the merge deleted, which is exactly the vacuous coverage this review keeps finding.
    /// </remarks>
    [Fact]
    public async Task SignInMergesTheParkedClaimsAndReusesTheParkedSid()
    {
        var captured = new CapturingAuthenticationService();
        var httpContext = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddSingleton<IAuthenticationService>(captured)
                .AddSingleton<IServiceProviderIsService>(new NoServiceIsService())
                .BuildServiceProvider(),
        };

        var user = new Authagonal.Core.Models.AuthUser
        {
            Id = Subject,
            Email = "ceo@acme.com",
            NormalizedEmail = "CEO@ACME.COM",
            SecurityStamp = "stamp",
        };

        var bound = DateTimeOffset.UtcNow.AddHours(8);
        await CookieSignInHelper.SignInAsync(
            httpContext, user, mfaAuthenticated: true,
            extraClaims: FederationClaims(), cookieExpiresUtc: bound, sessionId: "sid-from-callback");

        Assert.NotNull(captured.Principal);
        var claims = captured.Principal!.Claims
            .ToDictionary(c => c.Type, c => c.Value, StringComparer.Ordinal);

        Assert.Equal("ceo@acme.com", claims["saml_name_id"]);
        Assert.Equal("1800000000", claims["session_max_exp"]);
        Assert.Equal("Research", claims["federated:department"]);

        // The callback's sid, not a fresh one — the upstream refresh token is stored under it.
        Assert.Equal("sid-from-callback", claims["sid"]);

        // And the IdP's bound reached the cookie.
        Assert.Equal(bound.ToUnixTimeSeconds(), captured.Properties!.ExpiresUtc!.Value.ToUnixTimeSeconds());
    }

    /// <summary>
    /// A parked claim cannot displace one the helper mints itself.
    /// </summary>
    /// <remarks>
    /// The merge is additive for unseen types only. Otherwise parked state — which is written before MFA
    /// completes — could overwrite <c>sub</c> or <c>mfa_authenticated</c> on the session that MFA just
    /// established.
    /// </remarks>
    [Fact]
    public async Task ParkedClaimsCannotDisplaceTheMintedOnes()
    {
        var captured = new CapturingAuthenticationService();
        var httpContext = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddSingleton<IAuthenticationService>(captured)
                .AddSingleton<IServiceProviderIsService>(new NoServiceIsService())
                .BuildServiceProvider(),
        };

        var user = new Authagonal.Core.Models.AuthUser
        {
            Id = Subject, Email = "real@acme.com", NormalizedEmail = "REAL@ACME.COM", SecurityStamp = "stamp",
        };

        await CookieSignInHelper.SignInAsync(
            httpContext, user, mfaAuthenticated: true,
            extraClaims: [new Claim("sub", "someone-else"), new Claim("mfa_authenticated", "false")]);

        var subs = captured.Principal!.FindAll("sub").Select(c => c.Value).ToList();
        Assert.Equal([Subject], subs);
        Assert.Equal("true", captured.Principal.FindFirst("mfa_authenticated")!.Value);
    }

    private sealed class CapturingAuthenticationService : IAuthenticationService
    {
        public ClaimsPrincipal? Principal { get; private set; }
        public AuthenticationProperties? Properties { get; private set; }

        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme)
            => Task.FromResult(AuthenticateResult.NoResult());
        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
            => Task.CompletedTask;
        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
            => Task.CompletedTask;
        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties)
        {
            Principal = principal;
            Properties = properties;
            return Task.CompletedTask;
        }
        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
            => Task.CompletedTask;
    }

    private sealed class NoServiceIsService : IServiceProviderIsService
    {
        public bool IsService(Type serviceType) => false;
    }
}
