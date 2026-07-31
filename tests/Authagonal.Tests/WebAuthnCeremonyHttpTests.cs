using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Authagonal.Core.Models;
using Authagonal.Server.Endpoints;
using Authagonal.Server.Services;
using Authagonal.Tests.Infrastructure;
using Fido2NetLib;
using Microsoft.Extensions.DependencyInjection;

namespace Authagonal.Tests;

/// <summary>
/// The WebAuthn enrolment and passwordless ceremonies driven end to end over HTTP, with a software
/// authenticator producing genuinely valid attestations and assertions.
/// </summary>
/// <remarks>
/// <para>
/// WebAuthnRoundTripTests already exercises the fido2-net-lib verification path through
/// <see cref="WebAuthnService"/>. What it cannot reach is the part a browser client actually consumes:
/// how the endpoints translate a refusal into a status code and an error string. Three of those
/// mappings carry real weight — a duplicate credential id must be `409 credential_already_registered`
/// rather than a generic attestation failure, a passwordless assertion with no user handle must be
/// `401 user_handle_required`, and one naming another account must be refused — and each is a thin
/// hand-written branch, which is exactly the kind of code that survives a refactor by being silently
/// skipped.
/// </para>
/// <para>
/// Every refusal test is paired with a success case using the same fixture, so a 401 or 409 has to be
/// produced by the check under test rather than by a ceremony that was broken from the start. Without
/// the pairing these tests would pass just as happily against a server that refused everything.
/// </para>
/// <para>
/// Measured against the pre-fix code, three of these fail and each returns <c>200 OK</c>: the same-user
/// duplicate enrolment, the passwordless assertion with no user handle, and the one naming another
/// account. The two success cases pass either way — that is their job — and the cross-user duplicate
/// passes either way because that half of §7.1 step 22 was already implemented.
/// </para>
/// <para>
/// Two properties this fixture depends on are proved by it rather than asserted: the request host must
/// really be the allowlisted one, or <c>/webauthn/setup</c> would throw before returning options, and
/// the scheme must really be https, or the origin the authenticator signs over would not match the one
/// the server derives and every attestation would be refused.
/// </para>
/// </remarks>
public sealed class WebAuthnCeremonyHttpTests : IAsyncDisposable
{
    // The relying party is the request host, and the host must be in Auth:WebAuthnAllowedHosts. Driving
    // the client at the issuer host rather than TestServer's default http://localhost is what makes the
    // allowlist a live part of these tests instead of a bypassed one, and it keeps the ceremony on https
    // so the session cookie is stored and replayed the way a browser would.
    private const string RpId = "test.authagonal.local";
    private const string Origin = "https://test.authagonal.local";

    private readonly AuthagonalTestFactory _factory = new()
    {
        ConfigureAuthOptions = o => o.WebAuthnAllowedHosts = [RpId],
    };

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private HttpClient CreateClient()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.BaseAddress = new Uri(Origin + "/");
        return client;
    }

    /// <summary>
    /// A signed-in session for a user who already holds a confirmed TOTP credential, which passkey
    /// enrolment requires. Goes through login and the real MFA verify step rather than forging a
    /// cookie, so the enrolment endpoints are reached the way the login app reaches them.
    /// </summary>
    private async Task<(HttpClient Client, AuthUser User)> SignedInUserAsync(string email = "test@example.com")
    {
        await _factory.SeedTestDataAsync();
        var user = await _factory.SeedTestUserAsync(email);
        // Login only challenges when the user is MFA-enabled; going through the challenge rather than
        // around it means the enrolment endpoints are reached with the same post-MFA session the login
        // app holds, not a shortcut cookie.
        user.MfaEnabled = true;
        await _factory.UserStore.UpdateAsync(user);

        var totp = _factory.Services.GetRequiredService<TotpService>();
        var secret = totp.GenerateSecret();
        await _factory.MfaStore.CreateCredentialAsync(new MfaCredential
        {
            Id = Guid.NewGuid().ToString("N"),
            UserId = user.Id,
            Type = MfaCredentialType.Totp,
            Name = "Authenticator app",
            SecretProtected = Convert.ToBase64String(secret), // PlaintextSecretProvider in tests
            CreatedAt = DateTimeOffset.UtcNow,
        });

        var client = CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password = "Test1234!" });
        login.EnsureSuccessStatusCode();
        var challengeId = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("challengeId").GetString();

        var verify = await client.PostAsJsonAsync("/api/auth/mfa/verify", new
        {
            challengeId,
            method = "totp",
            code = totp.GenerateCode(secret),
        });
        verify.EnsureSuccessStatusCode();

        return (client, user);
    }

    /// <summary>Runs the full enrolment ceremony and returns the confirm response, unexamined.</summary>
    private static async Task<HttpResponseMessage> EnrolPasskeyAsync(HttpClient client, VirtualAuthenticator auth)
    {
        var setup = await client.PostAsync("/api/auth/mfa/webauthn/setup", null);
        setup.EnsureSuccessStatusCode();
        var setupJson = await setup.Content.ReadFromJsonAsync<JsonElement>();
        var setupToken = setupJson.GetProperty("setupToken").GetString();
        var options = CredentialCreateOptions.FromJson(setupJson.GetProperty("options").GetRawText());

        return await client.PostAsJsonAsync("/api/auth/mfa/webauthn/confirm", new
        {
            setupToken,
            attestationResponse = JsonSerializer.Serialize(auth.Attestation(options.Challenge)),
        });
    }

    private static async Task<string> ErrorOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()!;

    // ── enrolment ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The fixture itself: without a working enrolment over HTTP, every refusal assertion below could
    /// be produced by a ceremony that never worked.
    /// </summary>
    [Fact]
    public async Task Enrolment_OverHttp_Succeeds_AndIndexesTheCredential()
    {
        var (client, user) = await SignedInUserAsync();
        var auth = new VirtualAuthenticator(RpId, Origin);

        var confirm = await EnrolPasskeyAsync(client, auth);
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);

        var credentialId = (await confirm.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("credentialId").GetString();
        Assert.NotNull(credentialId);

        // The credential-id index is what passwordless login resolves against, so enrolment is only
        // complete if it points at this user's new credential.
        var owner = await _factory.MfaStore.FindByWebAuthnCredentialIdAsync(auth.CredentialId);
        Assert.Equal((user.Id, credentialId), owner);

        var stored = await _factory.MfaStore.GetCredentialsAsync(user.Id);
        Assert.Single(stored.Where(c => c.Type == MfaCredentialType.WebAuthn && MfaSetupEndpoints.IsConfirmed(c)));
    }

    /// <summary>
    /// The same user re-enrolling an authenticator they already hold. Nothing rejected this before: it
    /// produced a second credential row for one credential id, its signature counter reset to the
    /// attestation's, sharing the single index row that either row's deletion would remove.
    /// </summary>
    /// <remarks>
    /// The status code is the point. With the uniqueness callback stubbed to <c>true</c> this returned
    /// <c>200</c> and duplicated the row; with the callback fixed but the mapping missing it would be a
    /// generic <c>400 attestation_failed</c>, which tells a client nothing about what to do.
    /// </remarks>
    [Fact]
    public async Task Enrolment_OfACredentialIdThisUserAlreadyHolds_Is409()
    {
        var (client, user) = await SignedInUserAsync();
        var auth = new VirtualAuthenticator(RpId, Origin);

        var first = await EnrolPasskeyAsync(client, auth);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstCredentialId = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("credentialId").GetString();

        var second = await EnrolPasskeyAsync(client, auth);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("credential_already_registered", await ErrorOf(second));

        // The refusal created no second passkey. Pending rows are excluded because /webauthn/setup
        // writes one before the ceremony can possibly be judged, and the refused attempt leaves its
        // own behind — the next setup call reaps it. What must not exist is a second CONFIRMED
        // credential, which is what the duplicate produced before.
        var confirmed = (await _factory.MfaStore.GetCredentialsAsync(user.Id))
            .Where(c => c.Type == MfaCredentialType.WebAuthn && MfaSetupEndpoints.IsConfirmed(c))
            .ToList();
        Assert.Single(confirmed);
        Assert.Equal(firstCredentialId, confirmed[0].Id);

        // And the index still resolves to the original row rather than having been repointed.
        Assert.Equal((user.Id, firstCredentialId),
            await _factory.MfaStore.FindByWebAuthnCredentialIdAsync(auth.CredentialId));
    }

    /// <summary>
    /// A credential id already claimed by a DIFFERENT account. The index row is what makes a credential
    /// id resolve to one user, so admitting this would repoint another account's passwordless login.
    /// </summary>
    /// <remarks>
    /// Unlike its siblings this one passes against the pre-fix code too — the cross-user case was the
    /// half that WAS implemented, as a read before the write. It is here as a regression guard, not as
    /// evidence: what changed underneath it is that the verdict now comes from the conditional claim
    /// rather than from a read a concurrent registration could slip between.
    /// </remarks>
    [Fact]
    public async Task Enrolment_OfACredentialIdAnotherUserHolds_Is409_AndLeavesTheIndexAlone()
    {
        var (client, _) = await SignedInUserAsync();
        var auth = new VirtualAuthenticator(RpId, Origin);

        // Someone else got there first.
        Assert.True(await _factory.MfaStore.TryStoreWebAuthnCredentialIdMappingAsync(
            auth.CredentialId, "other-user", "other-credential"));

        var confirm = await EnrolPasskeyAsync(client, auth);
        Assert.Equal(HttpStatusCode.Conflict, confirm.StatusCode);
        Assert.Equal("credential_already_registered", await ErrorOf(confirm));

        Assert.Equal(("other-user", "other-credential"),
            await _factory.MfaStore.FindByWebAuthnCredentialIdAsync(auth.CredentialId));
    }

    // ── passwordless ─────────────────────────────────────────────────────────────

    private async Task<(string ChallengeId, AssertionOptions Options)> PasswordlessBeginAsync(HttpClient client)
    {
        var begin = await client.PostAsync("/api/auth/mfa/passwordless/begin", null);
        begin.EnsureSuccessStatusCode();
        var json = await begin.Content.ReadFromJsonAsync<JsonElement>();
        return (json.GetProperty("challengeId").GetString()!,
                AssertionOptions.FromJson(json.GetProperty("options").GetRawText()));
    }

    private static Task<HttpResponseMessage> PasswordlessCompleteAsync(
        HttpClient client, string challengeId, AuthenticatorAssertionRawResponse assertion) =>
        client.PostAsJsonAsync("/api/auth/mfa/passwordless/complete", new
        {
            challengeId,
            assertion = JsonSerializer.Serialize(assertion),
        });

    /// <summary>
    /// The paired success case. Everything the two refusals below hold constant — enrolment, challenge,
    /// signature, origin, RP-ID hash, user verification, counter — is proved to work here, so those
    /// tests fail on the user handle alone.
    /// </summary>
    [Fact]
    public async Task Passwordless_WithTheOwnersUserHandle_SignsIn()
    {
        var (client, user) = await SignedInUserAsync();
        var auth = new VirtualAuthenticator(RpId, Origin);
        Assert.Equal(HttpStatusCode.OK, (await EnrolPasskeyAsync(client, auth)).StatusCode);

        var anonymous = CreateClient();
        var (challengeId, options) = await PasswordlessBeginAsync(anonymous);

        var complete = await PasswordlessCompleteAsync(anonymous, challengeId,
            auth.Assertion(options.Challenge, signCount: 1, userHandle: Encoding.UTF8.GetBytes(user.Id)));

        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        var body = await complete.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(user.Id, body.GetProperty("userId").GetString());
    }

    /// <summary>
    /// WebAuthn §7.2 step 6 makes the user handle mandatory when the user was not identified before the
    /// ceremony. The endpoint never read it: the account came from the credential-id index alone, and
    /// Fido2NetLib skips the ownership callback entirely when no handle is present — so an assertion
    /// omitting it was verified with the ownership check silently not running.
    /// </summary>
    [Fact]
    public async Task Passwordless_WithNoUserHandle_Is401_UserHandleRequired()
    {
        var (client, _) = await SignedInUserAsync();
        var auth = new VirtualAuthenticator(RpId, Origin);
        Assert.Equal(HttpStatusCode.OK, (await EnrolPasskeyAsync(client, auth)).StatusCode);

        var anonymous = CreateClient();
        var (challengeId, options) = await PasswordlessBeginAsync(anonymous);

        var complete = await PasswordlessCompleteAsync(anonymous, challengeId,
            auth.Assertion(options.Challenge, signCount: 1, userHandle: null));

        Assert.Equal(HttpStatusCode.Unauthorized, complete.StatusCode);
        Assert.Equal("user_handle_required", await ErrorOf(complete));
    }

    /// <summary>
    /// A handle naming an account other than the credential's indexed owner. The signature still
    /// verifies against the owner's key, so this is refused on the handle and nothing else.
    /// </summary>
    [Fact]
    public async Task Passwordless_WithAUserHandleForAnotherAccount_Is401()
    {
        var (client, _) = await SignedInUserAsync();
        var auth = new VirtualAuthenticator(RpId, Origin);
        Assert.Equal(HttpStatusCode.OK, (await EnrolPasskeyAsync(client, auth)).StatusCode);

        var anonymous = CreateClient();
        var (challengeId, options) = await PasswordlessBeginAsync(anonymous);

        var complete = await PasswordlessCompleteAsync(anonymous, challengeId,
            auth.Assertion(options.Challenge, signCount: 1,
                userHandle: Encoding.UTF8.GetBytes("some-other-account")));

        Assert.Equal(HttpStatusCode.Unauthorized, complete.StatusCode);
        Assert.Equal("credential_not_found", await ErrorOf(complete));
    }
}
