using System.Text.Json;
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
            return Results.Json(new { error = "challenge_required" }, statusCode: 400);

        if (string.IsNullOrWhiteSpace(request.Method))
            return Results.Json(new { error = "method_required" }, statusCode: 400);

        // Consume challenge (atomic — prevents replay)
        var challenge = await mfaStore.ConsumeChallengeAsync(request.ChallengeId, ct);
        if (challenge is null)
            return Results.Json(new { error = "invalid_challenge" }, statusCode: 400);

        var user = await userStore.GetAsync(challenge.UserId, ct);
        if (user is null)
            return Results.Json(new { error = "user_not_found" }, statusCode: 400);

        var credentials = await mfaStore.GetCredentialsAsync(challenge.UserId, ct);

        switch (request.Method.ToLowerInvariant())
        {
            case "totp":
            {
                if (string.IsNullOrWhiteSpace(request.Code))
                    return Results.Json(new { error = "code_required" }, statusCode: 400);

                var totpCred = credentials.FirstOrDefault(c => c.Type == MfaCredentialType.Totp);
                if (totpCred is null)
                    return Results.Json(new { error = "totp_not_enrolled" }, statusCode: 400);

                // Decrypt secret
                var secretBase64 = await secretProvider.ResolveAsync(totpCred.SecretProtected!, ct);
                var secret = Convert.FromBase64String(secretBase64);

                if (!totpService.VerifyCode(secret, request.Code))
                    return Results.Json(new { error = "invalid_code" }, statusCode: 401);

                totpCred.LastUsedAt = DateTimeOffset.UtcNow;
                await mfaStore.UpdateCredentialAsync(totpCred, ct);

                await authHooks.RunOnMfaVerifiedAsync(user.Id, user.Email, "totp", ct);
                break;
            }

            case "recovery":
            {
                if (string.IsNullOrWhiteSpace(request.Code))
                    return Results.Json(new { error = "code_required" }, statusCode: 400);

                var recoveryCreds = credentials
                    .Where(c => c.Type == MfaCredentialType.RecoveryCode && !c.IsConsumed)
                    .ToList();

                var matchedCred = recoveryCreds.FirstOrDefault(c =>
                    recoveryCodeService.VerifyCode(request.Code, c.SecretProtected!));

                if (matchedCred is null)
                    return Results.Json(new { error = "invalid_code" }, statusCode: 401);

                matchedCred.IsConsumed = true;
                matchedCred.LastUsedAt = DateTimeOffset.UtcNow;
                await mfaStore.UpdateCredentialAsync(matchedCred, ct);

                await authHooks.RunOnMfaVerifiedAsync(user.Id, user.Email, "recovery", ct);
                break;
            }

            case "webauthn":
            {
                if (string.IsNullOrWhiteSpace(request.Assertion))
                    return Results.Json(new { error = "assertion_required" }, statusCode: 400);

                if (string.IsNullOrWhiteSpace(challenge.WebAuthnChallenge))
                    return Results.Json(new { error = "webauthn_not_available" }, statusCode: 400);

                var assertionOptions = AssertionOptions.FromJson(challenge.WebAuthnChallenge);
                AuthenticatorAssertionRawResponse assertionResponse;
                try
                {
                    assertionResponse = JsonSerializer.Deserialize<AuthenticatorAssertionRawResponse>(request.Assertion)!;
                }
                catch
                {
                    return Results.Json(new { error = "invalid_assertion" }, statusCode: 400);
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
                    var data = JsonSerializer.Deserialize<WebAuthnCredentialData>(wc.PublicKeyJson);
                    if (data?.CredentialId == assertedCredentialId)
                    {
                        matchedWebAuthnCred = wc;
                        credData = data;
                        break;
                    }
                }

                if (matchedWebAuthnCred is null || credData is null)
                    return Results.Json(new { error = "credential_not_found" }, statusCode: 401);

                var storedPublicKey = Convert.FromBase64String(credData.PublicKey);
                var (success, _, newSignCount) = await webAuthnService.CompleteAssertionAsync(
                    assertionOptions, assertionResponse, storedPublicKey, matchedWebAuthnCred.SignCount, ct);

                if (!success)
                    return Results.Json(new { error = "assertion_failed" }, statusCode: 401);

                matchedWebAuthnCred.SignCount = newSignCount;
                matchedWebAuthnCred.LastUsedAt = DateTimeOffset.UtcNow;
                await mfaStore.UpdateCredentialAsync(matchedWebAuthnCred, ct);

                await authHooks.RunOnMfaVerifiedAsync(user.Id, user.Email, "webauthn", ct);
                break;
            }

            default:
                return Results.Json(new { error = "unsupported_method" }, statusCode: 400);
        }

        // MFA verified — sign cookie
        await CookieSignInHelper.SignInAsync(httpContext, user);

        var name = CookieSignInHelper.GetDisplayName(user);
        logger.LogInformation("User {UserId} ({Email}) signed in via MFA ({Method})", user.Id, user.Email, request.Method);

        await authHooks.RunOnUserAuthenticatedAsync(user.Id, user.Email, "password", challenge.ClientId, ct);

        return Results.Ok(new { userId = user.Id, email = user.Email, name });
    }
}

public sealed record MfaVerifyRequest(string? ChallengeId, string? Method, string? Code, string? Assertion);
