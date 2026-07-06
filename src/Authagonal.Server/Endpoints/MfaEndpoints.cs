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
        ILogger<Program> logger,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ChallengeId))
            return JsonResults.Error("challenge_required");

        if (string.IsNullOrWhiteSpace(request.Method))
            return JsonResults.Error("method_required");

        // Consume challenge (atomic — prevents replay)
        var challenge = await mfaStore.ConsumeChallengeAsync(request.ChallengeId, ct);
        if (challenge is null)
            return JsonResults.Error("invalid_challenge");

        var user = await userStore.GetAsync(challenge.UserId, ct);
        if (user is null)
            return JsonResults.Error("user_not_found");

        var credentials = await mfaStore.GetCredentialsAsync(challenge.UserId, ct);

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
                {
                    await authHooks.RunOnMfaVerifyFailedAsync(user.Id, user.Email, "totp", ct);
                    return JsonResults.Error("invalid_code", 401);
                }

                totpCred.LastTotpStep = matchedStep;
                totpCred.LastUsedAt = DateTimeOffset.UtcNow;
                await mfaStore.UpdateCredentialAsync(totpCred, ct);

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

                var matchedCred = recoveryCreds.FirstOrDefault(c =>
                    recoveryCodeService.VerifyCode(request.Code, c.SecretProtected!));

                if (matchedCred is null)
                {
                    await authHooks.RunOnMfaVerifyFailedAsync(user.Id, user.Email, "recovery", ct);
                    return JsonResults.Error("invalid_code", 401);
                }

                matchedCred.IsConsumed = true;
                matchedCred.LastUsedAt = DateTimeOffset.UtcNow;
                await mfaStore.UpdateCredentialAsync(matchedCred, ct);

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
                        assertionOptions, assertionResponse, storedPublicKey, matchedWebAuthnCred.SignCount, ct);
                }
                catch (Fido2VerificationException)
                {
                    // Fido2NetLib throws on a failed/forged assertion or a sign-count regression
                    // (cloned authenticator). Surface it as a clean 401, not an unhandled 500.
                    await authHooks.RunOnMfaVerifyFailedAsync(user.Id, user.Email, "webauthn", ct);
                    return JsonResults.Error("assertion_failed", 401);
                }

                if (!success)
                {
                    await authHooks.RunOnMfaVerifyFailedAsync(user.Id, user.Email, "webauthn", ct);
                    return JsonResults.Error("assertion_failed", 401);
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
        var options = webAuthnService.CreateAssertionOptions([]);
        var challenge = new MfaChallenge
        {
            ChallengeId = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
            UserId = "",
            WebAuthnChallenge = options.ToJson(),
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

        var user = await userStore.GetAsync(userId, ct);
        if (user is null || !user.IsActive) return JsonResults.Error("credential_not_found", 401);

        // SSO bypass guard: if the tenant routes this user's domain to an external IdP (forced SSO), a
        // local passkey must NOT sidestep it (and its 2FA / conditional access / deprovisioning). Force
        // the IdP. Non-routed domains (optional/social connections) are unaffected — password + passkey OK.
        var domain = user.Email.Split('@', 2).LastOrDefault()?.ToLowerInvariant();
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
                assertionOptions, assertionResponse, storedPublicKey, cred.SignCount, ct);
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
