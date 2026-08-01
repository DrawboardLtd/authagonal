using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.Server.Services;
using Fido2NetLib;

namespace Authagonal.Server.Endpoints;

public static class MfaEndpoints
{
    public static IEndpointRouteBuilder MapMfaEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth/mfa");

        group.MapPost("/verify", VerifyAsync).AllowAnonymous().DisableAntiforgery();

        // Passwordless passkey login (conditional mediation): begin issues a discoverable-credential
        // assertion challenge; complete resolves the user FROM the passkey and signs them in.
        group.MapPost("/passwordless/begin", PasswordlessBeginAsync).AllowAnonymous().DisableAntiforgery();
        group.MapPost("/passwordless/complete", PasswordlessCompleteAsync).AllowAnonymous().DisableAntiforgery();

        return app;
    }

    private static async Task<IResult> VerifyAsync(
        MfaVerifyRequest request,
        HttpContext httpContext,
        IMfaStore mfaStore,
        IUserStore userStore,
        ISecretProvider secretProvider,
        TotpService totpService,
        RecoveryCodeService recoveryCodeService,
        WebAuthnService webAuthnService,
        IEnumerable<IAuthHook> authHooks,
        IRateLimiter rateLimiter,
        Microsoft.Extensions.Options.IOptions<AuthOptions> authOptions,
        ILogger<Program> logger,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ChallengeId))
            return JsonResults.Error("challenge_required");

        if (string.IsNullOrWhiteSpace(request.Method))
            return JsonResults.Error("method_required");

        // Load the challenge WITHOUT consuming: validate the code first, consume only on success — a
        // wrong code must NOT burn the one-time challenge, or the honest retry finds nothing ("Verification
        // session expired"). The store returns null for missing/expired/already-consumed, so this is still
        // replay-safe; a bounded attempt counter (below) preserves brute-force protection.
        var challenge = await mfaStore.GetChallengeAsync(request.ChallengeId, ct);
        if (challenge is null)
            return JsonResults.Error("invalid_challenge");

        // Only a verification challenge proves a second factor here. An enrolment token belongs to the
        // /api/auth/mfa/* setup endpoints, and a passwordless-discovery challenge identifies nobody and is
        // redeemed at /passwordless/complete.
        if (challenge.Purpose != MfaChallengePurpose.Verify)
            return JsonResults.Error("invalid_challenge");

        var user = await userStore.GetAsync(challenge.UserId, ct);
        if (user is null)
            return JsonResults.Error("user_not_found");

        // Per-subject request ceiling. The per-challenge Attempts counter below is a read-modify-write
        // against a blind full-row upsert in every provider (no ETag, no version column, and IMfaStore
        // exposes no atomic increment), so concurrent guesses all read the same value and all write the
        // same value — a measured ~6x amplification in-process, and far wider against a real store where
        // the window spans a network round trip. This gate does not race, and it is the bound that
        // actually holds.
        if (await rateLimiter.IsRateLimitedAsync($"mfa-verify|{challenge.UserId}", 10, TimeSpan.FromMinutes(1), ct))
            return JsonResults.Error("too_many_attempts", 429);

        // An account already locked out by failed MFA (or failed password) attempts cannot be verified
        // against — otherwise the lockout applies to the password step only and MFA guessing continues.
        if (user.LockoutEnabled && user.LockoutEnd.HasValue && user.LockoutEnd > DateTimeOffset.UtcNow)
            return JsonResults.Error("locked_out", 423);

        // Only CONFIRMED factors can satisfy a verification. A pending enrolment row holds a live TOTP
        // seed or registration challenge from an attempt the user never completed; accepting one would
        // make an abandoned enrolment a permanent second factor.
        var credentials = (await mfaStore.GetCredentialsAsync(challenge.UserId, ct))
            .Where(MfaSetupEndpoints.IsConfirmed)
            .ToList();

        // How many wrong guesses a single challenge tolerates before it's burned (→ fresh login).
        const int maxAttempts = 5;
        // A failed verify: fire the hook, count the attempt, and consume the challenge only once the
        // budget is spent. Below the budget, re-store the incremented challenge so the same challenge can
        // be retried (e.g. after a mistyped TOTP digit). Bounds TOTP brute-force to maxAttempts/1e6.
        async Task<IResult> FailAttemptAsync(string method, string errorCode)
        {
            await authHooks.RunOnMfaVerifyFailedAsync(user.Id, user.Email, method, ct);

            // Durable, atomic and cluster-wide: the SAME counter the password step uses. Every provider
            // implements this with a conditional write plus retry (ETag / version column), so unlike the
            // per-challenge counter below it cannot be defeated by concurrency — and unlike the
            // per-challenge counter it survives the attacker minting a fresh challenge, which was the gap
            // that made "5 guesses per challenge" no bound at all.
            var opts = authOptions.Value;
            var lockedOut = await userStore.RecordFailedLoginAsync(
                user.Id, opts.MaxFailedAttempts, TimeSpan.FromMinutes(opts.LockoutDurationMinutes), ct);
            if (lockedOut)
            {
                await mfaStore.ConsumeChallengeAsync(challenge.ChallengeId, ct);
                return JsonResults.Error("locked_out", 423);
            }

            // Best-effort per-challenge budget, kept for the fast path (a mistyped digit should not need a
            // fresh login). Known to be non-atomic; the two gates above are what bound an attacker.
            challenge.Attempts++;
            if (challenge.Attempts >= maxAttempts)
            {
                await mfaStore.ConsumeChallengeAsync(challenge.ChallengeId, ct);
                return JsonResults.Error("too_many_attempts", 401);
            }
            await mfaStore.StoreChallengeAsync(challenge, ct);
            return JsonResults.Error(errorCode, 401);
        }

        switch (request.Method.ToLowerInvariant())
        {
            case "totp":
            {
                if (string.IsNullOrWhiteSpace(request.Code))
                    return JsonResults.Error("code_required");

                var totpCred = credentials.FirstOrDefault(c => c.Type == MfaCredentialType.Totp);
                if (totpCred is null)
                    return JsonResults.Error("totp_not_enrolled");

                // Decrypt secret
                var secretBase64 = await secretProvider.ResolveAsync(totpCred.SecretProtected!, ct);
                var secret = Convert.FromBase64String(secretBase64);

                // Reject a code whose time-step was already used (replay within the validity window).
                var matchedStep = totpService.GetMatchingStep(secret, request.Code, totpCred.LastTotpStep ?? long.MinValue);
                if (matchedStep is null)
                    return await FailAttemptAsync("totp", "invalid_code");

                // The match above was decided against a value read before this request began, so on its
                // own it only rejects a SEQUENTIAL replay: N requests carrying the same captured code all
                // read the same step, all matched, and all used to write it back and succeed. That is the
                // real-time-phishing case RFC 6238 §5.2 exists for — the proxy replays the victim's code
                // alongside the victim's own login and both get in. Spending the step has to be the write.
                // Winning the claim IS the persist — there is deliberately no follow-up write here, since
                // an unconditional one would put back the lost update the claim exists to prevent.
                if (!await mfaStore.TryClaimTotpStepAsync(user.Id, totpCred.Id, matchedStep.Value, ct))
                    return await FailAttemptAsync("totp", "invalid_code");

                await authHooks.RunOnMfaVerifiedAsync(user.Id, user.Email, "totp", ct);
                break;
            }

            case "recovery":
            {
                if (string.IsNullOrWhiteSpace(request.Code))
                    return JsonResults.Error("code_required");

                var recoveryCreds = credentials
                    .Where(c => c.Type == MfaCredentialType.RecoveryCode && !c.IsConsumed)
                    .ToList();

                // F35: the stored code hash is encrypted at rest — resolve it before comparing.
                // ResolveAsync passes legacy (pre-encryption) plaintext hashes through unchanged, so old
                // recovery codes keep working without a backfill.
                // Retire the unsalted-SHA-256 form the moment this path touches it, for the WHOLE set —
                // not just the code being redeemed. Those digests are one SHA-256 of a 40-bit code with
                // no salt, so a single GPU pass over a store read recovers every enrolled user's live
                // recovery codes at once, and nothing was ever going to remove them because a user who
                // does not exhaust their codes never regenerates. The KDF is applied to the digest, so
                // the user's printed codes keep working; only the offline economics change. Best-effort:
                // a failed rewrite must not stop the person in front of us from getting in.
                foreach (var c in recoveryCreds)
                {
                    var current = await secretProvider.ResolveAsync(c.SecretProtected!, ct);
                    if (recoveryCodeService.UpgradeLegacyHash(current) is not { } upgraded) continue;

                    try
                    {
                        c.SecretProtected = await secretProvider.ProtectAsync(
                            $"mfa-recovery-{user.Id}-{c.Id}", upgraded, ct);
                        await mfaStore.UpdateCredentialAsync(c, ct);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Could not upgrade the stored hash of recovery credential {CredentialId}", c.Id);
                    }
                }

                MfaCredential? matchedCred = null;
                foreach (var c in recoveryCreds)
                {
                    var storedHash = await secretProvider.ResolveAsync(c.SecretProtected!, ct);
                    if (recoveryCodeService.VerifyCode(request.Code, storedHash))
                    {
                        matchedCred = c;
                        break;
                    }
                }

                if (matchedCred is null)
                    return await FailAttemptAsync("recovery", "invalid_code");

                // Same race as TOTP above, on a code that is a single-use bypass of the entire second
                // factor: two requests presenting the same one both saw IsConsumed = false and both
                // blind-wrote it true, so one code admitted two sessions. The flip has to be the claim.
                if (!await mfaStore.TryConsumeRecoveryCodeAsync(user.Id, matchedCred.Id, ct))
                    return await FailAttemptAsync("recovery", "invalid_code");

                await authHooks.RunOnMfaVerifiedAsync(user.Id, user.Email, "recovery", ct);
                break;
            }

            case "webauthn":
            {
                if (string.IsNullOrWhiteSpace(request.Assertion))
                    return JsonResults.Error("assertion_required");

                if (string.IsNullOrWhiteSpace(challenge.WebAuthnChallenge))
                    return JsonResults.Error("webauthn_not_available");

                var assertionOptions = AssertionOptions.FromJson(challenge.WebAuthnChallenge);
                AuthenticatorAssertionRawResponse assertionResponse;
                try
                {
                    assertionResponse = DeserializeFido2Assertion(request.Assertion);
                }
                catch
                {
                    return JsonResults.Error("invalid_assertion");
                }

                // Find matching credential by credential ID
                var webAuthnCreds = credentials.Where(c => c.Type == MfaCredentialType.WebAuthn).ToList();
                // assertionResponse.Id is Base64Url-encoded; convert to standard Base64 for comparison
                var assertedCredentialId = assertionResponse.Id
                    .Replace('-', '+').Replace('_', '/');
                switch (assertedCredentialId.Length % 4)
                {
                    case 2: assertedCredentialId += "=="; break;
                    case 3: assertedCredentialId += "="; break;
                }
                MfaCredential? matchedWebAuthnCred = null;
                WebAuthnCredentialData? credData = null;

                foreach (var wc in webAuthnCreds)
                {
                    if (wc.PublicKeyJson is null) continue;
                    var data = JsonSerializer.Deserialize(wc.PublicKeyJson!, AuthagonalJsonContext.Default.WebAuthnCredentialData);
                    if (data?.CredentialId == assertedCredentialId)
                    {
                        matchedWebAuthnCred = wc;
                        credData = data;
                        break;
                    }
                }

                if (matchedWebAuthnCred is null || credData is null)
                    return JsonResults.Error("credential_not_found", 401);

                var storedPublicKey = Convert.FromBase64String(credData.PublicKey);
                bool success;
                uint newSignCount;
                try
                {
                    (success, _, newSignCount) = await webAuthnService.CompleteAssertionAsync(
                        assertionOptions, assertionResponse, storedPublicKey, matchedWebAuthnCred.SignCount,
                        user.Id, credData.RpId, ct);
                }
                catch (Fido2VerificationException)
                {
                    // Fido2NetLib throws on a failed/forged assertion or a sign-count regression
                    // (cloned authenticator). Surface it as a clean 401, not an unhandled 500.
                    return await FailAttemptAsync("webauthn", "assertion_failed");
                }

                if (!success)
                {
                    return await FailAttemptAsync("webauthn", "assertion_failed");
                }

                matchedWebAuthnCred.SignCount = newSignCount;
                matchedWebAuthnCred.LastUsedAt = DateTimeOffset.UtcNow;
                await mfaStore.UpdateCredentialAsync(matchedWebAuthnCred, ct);

                await authHooks.RunOnMfaVerifiedAsync(user.Id, user.Email, "webauthn", ct);
                break;
            }

            default:
                return JsonResults.Error("unsupported_method");
        }

        // Verified — consume the challenge now (atomic delete, anti-replay) before issuing the session.
        await mfaStore.ConsumeChallengeAsync(challenge.ChallengeId, ct);

        // Clear the failure counter that FailAttemptAsync increments, so a user who mistypes a digit and
        // then succeeds is not left one attempt away from a lockout on their next sign-in. Same reset the
        // password step performs on success.
        await userStore.RecordSuccessfulLoginAsync(user.Id, ct: ct);

        // Run the onUserAuthenticated hook BEFORE establishing the session, so an enforced hook that
        // rejects the login prevents the cookie from being issued (not a 500 after it's already set).
        await authHooks.RunOnUserAuthenticatedAsync(user.Id, user.Email, "password", challenge.ClientId, ct);

        // MFA verified and not rejected — sign cookie with the MFA marker.
        await CookieSignInHelper.SignInAsync(httpContext, user, mfaAuthenticated: true);

        var name = CookieSignInHelper.GetDisplayName(user);
        logger.LogInformation("User {UserId} ({Email}) signed in via MFA ({Method})", user.Id, user.Email, request.Method);

        return TypedResults.Json(new UserIdentityResponse { UserId = user.Id, Email = user.Email, Name = name }, AuthagonalJsonContext.Default.UserIdentityResponse);
    }

    // Passwordless login step 1: issue an assertion challenge for DISCOVERABLE credentials (empty
    // allowCredentials), so the authenticator offers any resident passkey for this RP. No user context
    // yet — the user is resolved from the chosen credential at /complete.
    private static async Task<IResult> PasswordlessBeginAsync(
        IMfaStore mfaStore,
        WebAuthnService webAuthnService,
        CancellationToken ct)
    {
        // Passwordless: the passkey is the ONLY factor, so require user verification. Without it an
        // assertion proves possession of an unlocked device and nothing more, yet this path signs a session
        // marked mfa_authenticated.
        var options = webAuthnService.CreateAssertionOptions([], requireUserVerification: true);
        var challenge = new MfaChallenge
        {
            ChallengeId = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
            UserId = "",
            WebAuthnChallenge = options.ToJson(),
            // Minted to an anonymous caller and identifies nobody (UserId is empty). Tagged explicitly so
            // it can never be mistaken for proof of identity by the setup endpoints.
            Purpose = MfaChallengePurpose.PasswordlessDiscovery,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
        };
        await mfaStore.StoreChallengeAsync(challenge, ct);

        // Emit the options via Fido2's ToJson() as raw JSON (its own wire format; not the source-gen context).
        var body = new JsonObject
        {
            ["challengeId"] = challenge.ChallengeId,
            ["options"] = JsonNode.Parse(options.ToJson()),
        };
        return Results.Content(body.ToJsonString(), "application/json");
    }

    // Passwordless login step 2: verify the assertion, resolve the user from the credential, and sign in.
    private static async Task<IResult> PasswordlessCompleteAsync(
        PasswordlessCompleteRequest request,
        HttpContext httpContext,
        IMfaStore mfaStore,
        IUserStore userStore,
        ISsoDomainStore ssoDomainStore,
        WebAuthnService webAuthnService,
        IEnumerable<IAuthHook> authHooks,
        IRateLimiter rateLimiter,
        Microsoft.Extensions.Options.IOptions<AuthOptions> authOptions,
        ILogger<Program> logger,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ChallengeId))
            return JsonResults.Error("challenge_required");
        if (string.IsNullOrWhiteSpace(request.Assertion))
            return JsonResults.Error("assertion_required");

        // Consume the challenge atomically (anti-replay).
        var challenge = await mfaStore.ConsumeChallengeAsync(request.ChallengeId, ct);
        if (challenge is null || string.IsNullOrWhiteSpace(challenge.WebAuthnChallenge))
            return JsonResults.Error("invalid_challenge");

        // Only a discovery challenge may be redeemed here. This path resolves the user FROM the passkey
        // and signs them in with no password, so accepting a verification or enrolment challenge — both of
        // which are issued to a caller who named a user — would let that caller sign in as whoever owns
        // the asserted credential instead.
        if (challenge.Purpose != MfaChallengePurpose.PasswordlessDiscovery)
            return JsonResults.Error("invalid_challenge");

        AuthenticatorAssertionRawResponse assertionResponse;
        try { assertionResponse = DeserializeFido2Assertion(request.Assertion); }
        catch { return JsonResults.Error("invalid_assertion"); }

        // Resolve the owning user from the credential id (discoverable login carries no prior user).
        byte[] credentialIdBytes;
        try { credentialIdBytes = Base64UrlToBytes(assertionResponse.Id); }
        catch { return JsonResults.Error("invalid_assertion"); }

        var owner = await mfaStore.FindByWebAuthnCredentialIdAsync(credentialIdBytes, ct);
        if (owner is null) return JsonResults.Error("credential_not_found", 401);
        var (userId, credId) = owner.Value;

        // §7.2 step 6 — for a ceremony where the user was NOT identified beforehand, the user handle is
        // mandatory and must map to the credential's owner. Neither half was being done: the handle was
        // never read, and the account came solely from the credential-id index. The index is
        // authoritative and the signature is checked against that owner's key, so this was not a way in
        // — it is the tiebreaker the spec puts there for exactly the case where the index is ambiguous
        // or has been repointed, and it was absent. Checked before verification so a missing handle is a
        // clean refusal; the equality is re-asserted inside MakeAssertionAsync via the ownership
        // callback, which is the check the library will not perform without a handle to check.
        var userHandle = assertionResponse.Response.UserHandle;
        if (userHandle is null || userHandle.Length == 0)
            return JsonResults.Error("user_handle_required", 401);
        if (!userHandle.AsSpan().SequenceEqual(System.Text.Encoding.UTF8.GetBytes(userId)))
            return JsonResults.Error("credential_not_found", 401);

        var user = await userStore.GetAsync(userId, ct);
        if (user is null || !user.IsActive) return JsonResults.Error("credential_not_found", 401);

        // SSO bypass guard: if the tenant routes this user's domain to an external IdP (forced SSO), a
        // local passkey must NOT sidestep it (and its 2FA / conditional access / deprovisioning). Force
        // the IdP. Non-routed domains (optional/social connections) are unaffected — password + passkey OK.
        var domain = Authagonal.Core.Services.EmailDomain.Of(user.Email);
        if (!string.IsNullOrWhiteSpace(domain) && await ssoDomainStore.GetAsync(domain, ct) is { } ssoDomain)
        {
            var ssoRedirectUrl = ssoDomain.ProviderType.Equals("oidc", StringComparison.OrdinalIgnoreCase)
                ? $"/oidc/{ssoDomain.ConnectionId}/login"
                : $"/saml/{ssoDomain.ConnectionId}/login";
            return TypedResults.Json(new SsoRedirectError { Error = "sso_required", RedirectUrl = ssoRedirectUrl }, AuthagonalJsonContext.Default.SsoRedirectError, statusCode: 409);
        }

        var cred = await mfaStore.GetCredentialAsync(userId, credId, ct);
        if (cred is null || cred.Type != MfaCredentialType.WebAuthn || cred.PublicKeyJson is null)
            return JsonResults.Error("credential_not_found", 401);
        var credData = JsonSerializer.Deserialize(cred.PublicKeyJson!, AuthagonalJsonContext.Default.WebAuthnCredentialData);
        if (credData is null) return JsonResults.Error("credential_not_found", 401);

        var assertionOptions = AssertionOptions.FromJson(challenge.WebAuthnChallenge);
        var storedPublicKey = Convert.FromBase64String(credData.PublicKey);
        bool success;
        uint newSignCount;
        try
        {
            (success, _, newSignCount) = await webAuthnService.CompleteAssertionAsync(
                assertionOptions, assertionResponse, storedPublicKey, cred.SignCount, userId, credData.RpId, ct);
        }
        catch (Fido2VerificationException)
        {
            await authHooks.RunOnMfaVerifyFailedAsync(user.Id, user.Email, "webauthn", ct);
            return JsonResults.Error("assertion_failed", 401);
        }
        if (!success)
        {
            await authHooks.RunOnMfaVerifyFailedAsync(user.Id, user.Email, "webauthn", ct);
            return JsonResults.Error("assertion_failed", 401);
        }

        cred.SignCount = newSignCount;
        cred.LastUsedAt = DateTimeOffset.UtcNow;
        await mfaStore.UpdateCredentialAsync(cred, ct);

        // Enforcement hook before the session (an enforced onUserAuthenticated can reject the login).
        await authHooks.RunOnUserAuthenticatedAsync(user.Id, user.Email, "passkey", challenge.ClientId, ct);
        // A passkey is phishing-resistant strong auth, so sign in with the MFA marker — /connect/authorize
        // won't re-challenge.
        await CookieSignInHelper.SignInAsync(httpContext, user, mfaAuthenticated: true);
        logger.LogInformation("User {UserId} ({Email}) signed in passwordless via passkey", user.Id, user.Email);

        var name = CookieSignInHelper.GetDisplayName(user);
        return TypedResults.Json(new UserIdentityResponse { UserId = user.Id, Email = user.Email, Name = name }, AuthagonalJsonContext.Default.UserIdentityResponse);
    }

    private static byte[] Base64UrlToBytes(string base64Url)
    {
        var s = base64Url.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Fido2 external type")]
    private static AuthenticatorAssertionRawResponse DeserializeFido2Assertion(string json)
        => JsonSerializer.Deserialize<AuthenticatorAssertionRawResponse>(json)!;
}

public sealed class MfaVerifyRequest
{
    public string? ChallengeId { get; set; }
    public string? Method { get; set; }
    public string? Code { get; set; }
    public string? Assertion { get; set; }
}

public sealed class PasswordlessCompleteRequest
{
    public string? ChallengeId { get; set; }
    public string? Assertion { get; set; }
}
