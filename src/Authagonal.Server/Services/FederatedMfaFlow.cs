using System.Security.Claims;
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
        // Required, not optional-with-default: the enrolment branch sets a cookie on it, and a caller that
        // forgot to pass it would silently go back to putting the token in the URL.
        HttpContext httpContext,
        string returnUrl,
        string loginAppBase,
        IClientStore clientStore,
        IMfaStore mfaStore,
        WebAuthnService webAuthnService,
        IEnumerable<IAuthHook> authHooks,
        AuthOptions authOptions,
        ILogger logger,
        CancellationToken ct,
        IGrantStore? grantStore = null,
        IEnumerable<Claim>? federationClaims = null,
        DateTimeOffset? cookieExpires = null,
        string? sessionId = null)
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
                Purpose = MfaChallengePurpose.Verify,
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

            // Park the federation bindings against this challenge, so /api/auth/mfa/verify establishes the
            // SAME session this callback would have. Without it the verify path minted a bare cookie and every
            // binding was lost — including saml_name_id, which is how single logout finds a session, so
            // enabling MFA on a federated tenant quietly disabled SLO for its enrolled users. See
            // PendingFederatedSession.
            //
            // Best effort: this login has already authenticated upstream, and failing it here would be a worse
            // outcome than the (previous) degraded session.
            if (grantStore is not null && federationClaims is not null)
            {
                try
                {
                    await PendingFederatedSession.StoreAsync(
                        grantStore, challenge.ChallengeId, user.Id, clientId,
                        federationClaims, cookieExpires, challenge.ExpiresAt, ct, sessionId);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex,
                        "Could not park the federation state for user {UserId}; the session established after "
                        + "MFA will lack its federation bindings (SLO subject, IdP session bound, upstream token)",
                        user.Id);
                }
            }

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
                Purpose = MfaChallengePurpose.Enrol,
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(authOptions.MfaSetupTokenExpiryMinutes),
            };
            await mfaStore.StoreChallengeAsync(setupChallenge, ct);

            // Same parking for the enrolment branch: it also ends in a session established away from this
            // callback, so the same bindings would otherwise be lost.
            if (grantStore is not null && federationClaims is not null)
            {
                try
                {
                    await PendingFederatedSession.StoreAsync(
                        grantStore, setupChallenge.ChallengeId, user.Id, clientId,
                        federationClaims, cookieExpires, setupChallenge.ExpiresAt, ct, sessionId);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex,
                        "Could not park the federation state for user {UserId} during MFA enrolment", user.Id);
                }
            }

            logger.LogInformation("Federated login for user {UserId} requires MFA enrolment — redirecting to setup", user.Id);

            // The enrolment token goes in an HttpOnly cookie, NOT the URL.
            //
            // It used to ride the redirect as ?setupToken=…, which put a credential in a Location header,
            // in a real GET request line, in the browser's history, in the Referer of anything that page
            // loaded cross-origin, and in every access and proxy log in between. And it is a credential:
            // MfaSetupEndpoints.ResolveUserIdAsync accepts it as the sole identity for the enrolment
            // endpoints, and completing an enrolment it accepted signs a full session cookie. Anyone who
            // read it out of a log could enrol their own authenticator and sign in as the user.
            //
            // Expiry follows the challenge, so a stale cookie cannot outlive the token it carries.
            var setupCookie = MfaSetupEndpoints.SetupCookieOptions(httpContext);
            setupCookie.Expires = setupChallenge.ExpiresAt;
            httpContext.Response.Cookies.Append(
                MfaSetupEndpoints.SetupCookieName, setupChallenge.ChallengeId, setupCookie);

            var query = string.IsNullOrEmpty(returnUrl)
                ? QueryString.Empty
                : new QueryString().Add("returnUrl", returnUrl);

            return Results.Redirect($"{loginBase}/mfa-setup{query}");
        }

        // MFA neither enrolled nor required — federation is a sufficient sole factor.
        return null;
    }
}
