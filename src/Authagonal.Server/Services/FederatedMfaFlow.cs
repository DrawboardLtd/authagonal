using System.Security.Cryptography;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.Server.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Authagonal.Server.Services;

/// <summary>
/// F42 — routes a just-federated login through the local MFA challenge when the user's effective policy
/// demands it, instead of a federated session unconditionally satisfying MFA.
///
/// A SAML/OIDC assertion proves the FIRST factor (the upstream IdP authenticated the user). It does NOT
/// prove the SECOND factor that a tenant may require via <see cref="OAuthClient.MfaPolicy"/> /
/// <see cref="AuthUser.MfaEnabled"/>. So when MFA is enrolled (or required-but-not-yet-enrolled) we redirect
/// to the same login-app MFA pages the password path uses — carrying a challenge/setup token — and only mark
/// the session <c>mfa_authenticated</c> after that completes (via <c>/api/auth/mfa/verify</c>). When MFA is
/// neither enrolled nor required, federation stands alone and the caller signs the session directly.
/// </summary>
public static class FederatedMfaFlow
{
    /// <summary>
    /// Returns a redirect to the MFA challenge / setup page when the effective policy requires a second
    /// factor (the caller must NOT sign a full session in that case), or null when federation alone suffices
    /// (the caller proceeds to sign in). The <paramref name="returnUrl"/> is carried through so the user
    /// lands where they were headed after MFA completes.
    /// </summary>
    public static async Task<IResult?> MaybeChallengeAsync(
        AuthUser user,
        string returnUrl,
        string loginAppBase,
        IClientStore clientStore,
        IMfaStore mfaStore,
        WebAuthnService webAuthnService,
        IEnumerable<IAuthHook> authHooks,
        AuthOptions authOptions,
        ILogger logger,
        CancellationToken ct)
    {
        // The client (and thus its MfaPolicy) rides in the /authorize URL preserved as returnUrl.
        var clientId = AuthEndpoints.ExtractClientIdFromReturnUrl(returnUrl);
        var client = string.IsNullOrEmpty(clientId) ? null : await clientStore.GetAsync(clientId, ct);
        var clientPolicy = client?.MfaPolicy ?? MfaPolicy.Disabled;
        var effectivePolicy = await authHooks.RunResolveMfaPolicyAsync(user.Id, user.Email, clientPolicy, clientId ?? "", ct);

        var loginBase = loginAppBase.TrimEnd('/');

        // Enrolled → always challenge (MFA is a property of the user/session, not the requesting client).
        if (user.MfaEnabled)
        {
            var credentials = await mfaStore.GetCredentialsAsync(user.Id, ct);
            // Exclude half-finished enrolments and spent recovery codes (same rule as the password path).
            var confirmedCreds = credentials
                .Where(c => c.Name is not "TOTP (pending)" and not "WebAuthn (pending)")
                .ToList();
            var methods = confirmedCreds
                .Where(c => !c.IsConsumed)
                .Select(c => c.Type)
                .Distinct()
                .Select(t => t.ToString().ToLowerInvariant())
                .ToList();

            var challenge = new MfaChallenge
            {
                ChallengeId = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
                UserId = user.Id,
                ClientId = clientId,
                ReturnUrl = returnUrl,
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(authOptions.MfaChallengeExpiryMinutes),
            };

            // A passkey-options failure must never block login — the user can still use another factor.
            string? webAuthnJson = null;
            var webAuthnCreds = confirmedCreds.Where(c => c.Type == MfaCredentialType.WebAuthn).ToList();
            if (webAuthnCreds.Count > 0)
            {
                try
                {
                    challenge.WebAuthnChallenge = webAuthnService.CreateAssertionOptions(webAuthnCreds).ToJson();
                    webAuthnJson = challenge.WebAuthnChallenge;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to build WebAuthn assertion options for federated login of user {UserId}; continuing without the passkey option", user.Id);
                }
            }

            await mfaStore.StoreChallengeAsync(challenge, ct);
            logger.LogInformation("Federated login for user {UserId} requires MFA — redirecting to challenge", user.Id);

            var query = new QueryString()
                .Add("challengeId", challenge.ChallengeId);
            if (!string.IsNullOrEmpty(returnUrl))
                query = query.Add("returnUrl", returnUrl);
            if (methods.Count > 0)
                query = query.Add("methods", string.Join(',', methods));
            if (webAuthnJson is not null)
                query = query.Add("webAuthn", webAuthnJson);

            return Results.Redirect($"{loginBase}/mfa-challenge{query}");
        }

        // Not enrolled but the policy requires MFA → force enrolment before any session is issued.
        if (effectivePolicy == MfaPolicy.Required)
        {
            var setupChallenge = new MfaChallenge
            {
                ChallengeId = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
                UserId = user.Id,
                ClientId = clientId,
                ReturnUrl = returnUrl,
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(authOptions.MfaSetupTokenExpiryMinutes),
            };
            await mfaStore.StoreChallengeAsync(setupChallenge, ct);
            logger.LogInformation("Federated login for user {UserId} requires MFA enrolment — redirecting to setup", user.Id);

            var query = new QueryString().Add("setupToken", setupChallenge.ChallengeId);
            if (!string.IsNullOrEmpty(returnUrl))
                query = query.Add("returnUrl", returnUrl);

            return Results.Redirect($"{loginBase}/mfa-setup{query}");
        }

        // MFA neither enrolled nor required — federation is a sufficient sole factor.
        return null;
    }
}
