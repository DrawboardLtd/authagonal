using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.Server.Services;
using Fido2NetLib;
using Fido2NetLib.Objects;
using QRCoder;

namespace Authagonal.Server.Endpoints;

public static class MfaSetupEndpoints
{
    private const string SetupTokenHeader = "X-MFA-Setup-Token";

    /// <summary>Name marking a TOTP row that has been created but not yet proved by the user.</summary>
    internal const string PendingTotpName = "TOTP (pending)";

    /// <summary>Name marking a WebAuthn row holding registration options, not yet a real credential.</summary>
    internal const string PendingWebAuthnName = "WebAuthn (pending)";

    /// <summary>
    /// True when a credential is a real, proved factor rather than an in-progress enrolment row. Pending
    /// rows carry a live TOTP seed or registration challenge and must never count as a second factor —
    /// they are created before the user demonstrates anything, so an abandoned enrolment attempt would
    /// otherwise leave a usable credential behind.
    /// </summary>
    internal static bool IsConfirmed(MfaCredential c)
        => c.Name is not PendingTotpName and not PendingWebAuthnName;

    /// <summary>
    /// How long an unconfirmed enrolment row stays usable. <see cref="MfaCredential"/> carries no expiry
    /// column — the provider reapers sweep challenges, not credentials — so the bound is applied here
    /// against <see cref="MfaCredential.CreatedAt"/>. Generous next to the 15-minute setup token, since an
    /// enrolment ceremony involves the user installing an authenticator app, but finite: without it an
    /// abandoned attempt leaves a live seed or registration challenge on the account permanently.
    /// </summary>
    private static readonly TimeSpan PendingCredentialMaxAge = TimeSpan.FromMinutes(30);

    private static bool IsPendingExpired(MfaCredential c)
        => !IsConfirmed(c) && DateTimeOffset.UtcNow - c.CreatedAt > PendingCredentialMaxAge;

    public static IEndpointRouteBuilder MapMfaSetupEndpoints(this IEndpointRouteBuilder app)
    {
        // No .RequireAuthorization() — endpoints accept either cookie auth or setup token.
        // Each endpoint validates identity via ResolveUserIdAsync.
        var group = app.MapGroup("/api/auth/mfa");

        group.MapGet("/status", GetStatusAsync);
        group.MapPost("/totp/setup", TotpSetupAsync).DisableAntiforgery();
        group.MapPost("/totp/confirm", TotpConfirmAsync).DisableAntiforgery();
        group.MapPost("/webauthn/setup", WebAuthnSetupAsync).DisableAntiforgery();
        group.MapPost("/webauthn/confirm", WebAuthnConfirmAsync).DisableAntiforgery();
        group.MapPost("/recovery/generate", RecoveryGenerateAsync).DisableAntiforgery();
        group.MapDelete("/credentials/{credentialId}", DeleteCredentialAsync).DisableAntiforgery();

        return app;
    }

    /// <summary>
    /// Resolves user identity from cookie auth or X-MFA-Setup-Token header.
    /// Returns (userId, setupChallenge) where setupChallenge is non-null when authenticated via token.
    /// </summary>
    /// <remarks>
    /// This is the only identity gate in front of the enrolment endpoints, so it is where the purpose of
    /// a challenge is enforced. A challenge id is handed to a caller who has proved a password but holds
    /// no session, and login mints one for an already-enrolled user too — so accepting any live challenge
    /// here would let a password-only attacker drive factor enrolment against an enrolled victim
    /// (mint recovery codes, enrol their own passkey, brute-force TOTP at <c>/totp/confirm</c>). Only
    /// <see cref="MfaChallengePurpose.Enrol"/> is accepted, and a challenge that identifies nobody is
    /// refused outright.
    /// </remarks>
    private static async Task<(string? UserId, MfaChallenge? SetupChallenge)> ResolveUserIdAsync(
        HttpContext httpContext, IMfaStore mfaStore, CancellationToken ct)
    {
        // Try cookie auth first
        var userId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? httpContext.User.FindFirstValue("sub");
        if (userId is not null)
            return (userId, null);

        // Fall back to setup token
        var token = httpContext.Request.Headers[SetupTokenHeader].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(token))
            return (null, null);

        var challenge = await mfaStore.GetChallengeAsync(token, ct);
        if (challenge is null)
            return (null, null);

        // A verification challenge (the user already HAS a factor) or a passwordless-discovery challenge
        // (which identifies nobody) confers no enrolment authority. Only an enrolment token does.
        if (challenge.Purpose != MfaChallengePurpose.Enrol)
            return (null, null);

        // Defence in depth: a challenge with no subject cannot authenticate anybody. Callers guard on
        // `userId is null`, which an empty string would slip past — so reject it here.
        if (string.IsNullOrWhiteSpace(challenge.UserId))
            return (null, null);

        return (challenge.UserId, challenge);
    }

    private static async Task<IResult> GetStatusAsync(
        HttpContext httpContext,
        IMfaStore mfaStore,
        IClientStore clientStore,
        CancellationToken ct)
    {
        var (userId, _) = await ResolveUserIdAsync(httpContext, mfaStore, ct);
        if (userId is null) return Results.Unauthorized();

        // Is MFA offered for this tenant at all? False when every client's policy is Disabled — lets the
        // self-service setup UI hide itself (a tenant that has turned MFA off shouldn't be prompted to
        // enrol). Any non-Disabled client (Enabled/Required) means MFA is in play.
        var clients = await clientStore.GetAllAsync(ct);
        var offered = clients.Any(c => c.MfaPolicy != MfaPolicy.Disabled);

        var credentials = await mfaStore.GetCredentialsAsync(userId, ct);

        // Exclude pending setup credentials from status
        var confirmed = credentials
            .Where(IsConfirmed)
            .ToList();

        var methods = confirmed.Select(c => new MfaMethodInfo
        {
            Id = c.Id,
            Type = c.Type.ToString().ToLowerInvariant(),
            Name = c.Name,
            CreatedAt = c.CreatedAt,
            LastUsedAt = c.LastUsedAt,
            IsConsumed = c.Type == MfaCredentialType.RecoveryCode ? c.IsConsumed : (bool?)null,
        }).ToList();

        var enabled = confirmed.Any(c => c.Type != MfaCredentialType.RecoveryCode);

        return TypedResults.Json(new MfaStatusResponse { Enabled = enabled, Offered = offered, Methods = methods }, AuthagonalJsonContext.Default.MfaStatusResponse);
    }

    private static async Task<IResult> TotpSetupAsync(
        HttpContext httpContext,
        IMfaStore mfaStore,
        IUserStore userStore,
        TotpService totpService,
        ISecretProvider secretProvider,
        Authagonal.Core.Services.ITenantContext tenantContext,
        CancellationToken ct)
    {
        var (userId, _) = await ResolveUserIdAsync(httpContext, mfaStore, ct);
        if (userId is null) return Results.Unauthorized();

        // Get email — from cookie claims or user store
        var email = httpContext.User.FindFirstValue(ClaimTypes.Email);
        if (email is null)
        {
            var u = await userStore.GetAsync(userId, ct);
            email = u?.Email ?? "";
        }

        // Check if TOTP already enrolled (exclude pending setups)
        var credentials = await mfaStore.GetCredentialsAsync(userId, ct);

        // Clean up any orphaned pending TOTP setup credentials
        var pendingTotp = credentials
            .Where(c => c.Type == MfaCredentialType.Totp && c.Name == PendingTotpName)
            .ToList();
        foreach (var pending in pendingTotp)
            await mfaStore.DeleteCredentialAsync(userId, pending.Id, ct);

        if (credentials.Any(c => c.Type == MfaCredentialType.Totp && IsConfirmed(c)))
            return JsonResults.Error("totp_already_enrolled", 409);

        // Generate secret
        var secret = totpService.GenerateSecret();
        var issuer = tenantContext.Issuer;
        var otpAuthUri = totpService.GetOtpAuthUri(email, secret, issuer);

        // Generate QR code as PNG data URI
        string qrCodeDataUri;
        using (var qrGenerator = new QRCodeGenerator())
        {
            var qrData = qrGenerator.CreateQrCode(otpAuthUri, QRCodeGenerator.ECCLevel.M);
            var pngQr = new PngByteQRCode(qrData);
            var pngBytes = pngQr.GetGraphic(8);
            qrCodeDataUri = $"data:image/png;base64,{Convert.ToBase64String(pngBytes)}";
        }

        // Base32 key for manual entry
        var manualKey = TotpService.Base32Encode(secret);

        // Protect secret for storage
        var secretBase64 = Convert.ToBase64String(secret);
        var protectedSecret = await secretProvider.ProtectAsync($"mfa-totp-{userId}", secretBase64, ct);

        // Store as a setup token (credential not yet confirmed)
        var setupToken = Guid.NewGuid().ToString("N");

        // Store temporarily using a setup credential that's not yet "live"
        var setupCred = new MfaCredential
        {
            Id = setupToken,
            UserId = userId,
            Type = MfaCredentialType.Totp,
            Name = PendingTotpName,
            SecretProtected = protectedSecret,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await mfaStore.CreateCredentialAsync(setupCred, ct);

        return TypedResults.Json(new TotpSetupResponse { SetupToken = setupToken, QrCodeDataUri = qrCodeDataUri, ManualKey = manualKey }, AuthagonalJsonContext.Default.TotpSetupResponse);
    }

    private static async Task<IResult> TotpConfirmAsync(
        TotpConfirmRequest request,
        HttpContext httpContext,
        IMfaStore mfaStore,
        IUserStore userStore,
        TotpService totpService,
        ISecretProvider secretProvider,
        IRateLimiter rateLimiter,
        IEnumerable<IAuthHook> authHooks,
        CancellationToken ct)
    {
        var (userId, setupChallenge) = await ResolveUserIdAsync(httpContext, mfaStore, ct);
        if (userId is null) return Results.Unauthorized();

        if (string.IsNullOrWhiteSpace(request.SetupToken) || string.IsNullOrWhiteSpace(request.Code))
            return JsonResults.Error("invalid_request");

        // Find the pending credential. It MUST still be pending: this endpoint is a TOTP acceptance path
        // that issues a session, so allowing it against an already-confirmed credential would make it a
        // second, unthrottled /verify.
        var cred = await mfaStore.GetCredentialAsync(userId, request.SetupToken, ct);
        if (cred is null || cred.Type != MfaCredentialType.Totp || cred.Name != PendingTotpName)
            return JsonResults.Error("invalid_setup_token");

        // A stale pending seed is not enrollable — drop it and make the user restart, so an abandoned
        // attempt cannot be confirmed weeks later by whoever still holds the setup token.
        if (IsPendingExpired(cred))
        {
            await mfaStore.DeleteCredentialAsync(userId, cred.Id, ct);
            return JsonResults.Error("setup_expired");
        }

        // Throttle. This endpoint accepts a 6-digit code and issues a cookie, so without a bound it is a
        // brute-force oracle against a 10^6 space. Keyed on the subject, matching the budget /verify
        // applies per challenge.
        if (await rateLimiter.IsRateLimitedAsync($"mfa-totp-confirm|{userId}", 10, TimeSpan.FromMinutes(1), ct))
            return JsonResults.Error("too_many_attempts", 429);

        // Verify code against the stored secret
        var secretBase64 = await secretProvider.ResolveAsync(cred.SecretProtected!, ct);
        var secret = Convert.FromBase64String(secretBase64);

        // Take the matched step (not the bool wrapper) so the code can be burned below — otherwise the
        // enrolment code stays replayable at /verify for the rest of its window.
        var matchedStep = totpService.GetMatchingStep(secret, request.Code, cred.LastTotpStep ?? long.MinValue);
        if (matchedStep is null)
        {
            // Count the failure against the challenge budget, exactly as /verify does, so a wrong code here
            // is not a free guess.
            if (setupChallenge is not null)
            {
                setupChallenge.Attempts++;
                if (setupChallenge.Attempts >= 5)
                {
                    await mfaStore.ConsumeChallengeAsync(setupChallenge.ChallengeId, ct);
                    return JsonResults.Error("too_many_attempts", 401);
                }
                await mfaStore.StoreChallengeAsync(setupChallenge, ct);
            }
            return JsonResults.Error("invalid_code");
        }

        // Confirm: name it active AND burn the accepted step so this code cannot be replayed at /verify.
        cred.Name = "Authenticator app";
        cred.LastTotpStep = matchedStep;
        cred.LastUsedAt = DateTimeOffset.UtcNow;
        await mfaStore.UpdateCredentialAsync(cred, ct);

        // Set MfaEnabled on user
        var user = await userStore.GetAsync(userId, ct);
        if (user is not null && !user.MfaEnabled)
        {
            user.MfaEnabled = true;
            user.UpdatedAt = DateTimeOffset.UtcNow;
            await userStore.UpdateAsync(user, ct);
        }

        if (user is not null)
            await authHooks.RunOnMfaEnrolledAsync(user.Id, user.Email, "totp", ct);

        // If authenticated via setup token, sign the cookie now (user proved password + TOTP).
        // Run the onUserAuthenticated hook BEFORE signing in, so an enforced hook that rejects the
        // login prevents the cookie from being issued.
        if (setupChallenge is not null && user is not null)
        {
            await authHooks.RunOnUserAuthenticatedAsync(user.Id, user.Email, "password", setupChallenge.ClientId, ct);
            await CookieSignInHelper.SignInAsync(httpContext, user);
            await mfaStore.ConsumeChallengeAsync(setupChallenge.ChallengeId, ct);
        }

        return TypedResults.Json(new SuccessResponse(), AuthagonalJsonContext.Default.SuccessResponse);
    }

    private static async Task<IResult> WebAuthnSetupAsync(
        HttpContext httpContext,
        IMfaStore mfaStore,
        IUserStore userStore,
        ISsoDomainStore ssoDomainStore,
        WebAuthnService webAuthnService,
        CancellationToken ct)
    {
        var (userId, _) = await ResolveUserIdAsync(httpContext, mfaStore, ct);
        if (userId is null) return Results.Unauthorized();

        var user = await userStore.GetAsync(userId, ct);
        if (user is null) return Results.Unauthorized();

        // Don't let a user whose domain is SSO-routed (the tenant forces the IdP) enrol a local passkey —
        // it would become a bypass of the IdP and its deprovisioning. They authenticate via SSO instead.
        var ssoDomainName = user.Email.Split('@', 2).LastOrDefault()?.ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(ssoDomainName) && await ssoDomainStore.GetAsync(ssoDomainName, ct) is not null)
            return Results.Json(new ErrorInfoResponse { Error = "sso_managed" }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);

        var existingCredentials = await mfaStore.GetCredentialsAsync(userId, ct);

        // Clean up any orphaned pending WebAuthn setup credentials (from cancelled attempts)
        var pendingWebAuthn = existingCredentials
            .Where(c => c.Type == MfaCredentialType.WebAuthn && c.Name == PendingWebAuthnName)
            .ToList();
        foreach (var pending in pendingWebAuthn)
            await mfaStore.DeleteCredentialAsync(userId, pending.Id, ct);

        // Re-fetch if we cleaned up any
        if (pendingWebAuthn.Count > 0)
            existingCredentials = await mfaStore.GetCredentialsAsync(userId, ct);

        // Passkeys are a per-device convenience layered on top of the portable base factor (TOTP), not a
        // standalone factor. Require a confirmed TOTP credential before allowing passkey enrolment, so
        // every account keeps a device-independent factor and a "Required" MFA policy can't be satisfied
        // by a passkey alone (which would risk a lockout on a device the passkey isn't on). Enforced here
        // at the boundary, not just hidden in the client.
        var hasConfirmedTotp = existingCredentials.Any(c => c.Type == MfaCredentialType.Totp && IsConfirmed(c));
        if (!hasConfirmedTotp)
            return Results.Json(new ErrorInfoResponse { Error = "totp_required_first" }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);

        var (options, setupToken) = webAuthnService.CreateAttestationOptions(user, existingCredentials);

        // Store the options JSON temporarily in a credential so we can verify later
        var setupCred = new MfaCredential
        {
            Id = setupToken,
            UserId = userId,
            Type = MfaCredentialType.WebAuthn,
            Name = PendingWebAuthnName,
            PublicKeyJson = options.ToJson(),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await mfaStore.CreateCredentialAsync(setupCred, ct);

        // Fido2's CredentialCreateOptions isn't in the source-gen JSON context, and it has its own
        // converters for the WebAuthn wire format — so emit it via Fido2's ToJson() embedded as raw JSON
        // rather than letting the typed serializer choke on the object-typed Options member.
        var setupBody = new JsonObject
        {
            ["setupToken"] = setupToken,
            ["options"] = JsonNode.Parse(options.ToJson()),
        };
        return Results.Content(setupBody.ToJsonString(), "application/json");
    }

    private static async Task<IResult> WebAuthnConfirmAsync(
        WebAuthnConfirmRequest request,
        HttpContext httpContext,
        IMfaStore mfaStore,
        IUserStore userStore,
        WebAuthnService webAuthnService,
        IEnumerable<IAuthHook> authHooks,
        CancellationToken ct)
    {
        var (userId, setupChallenge) = await ResolveUserIdAsync(httpContext, mfaStore, ct);
        if (userId is null) return Results.Unauthorized();

        if (string.IsNullOrWhiteSpace(request.SetupToken) || string.IsNullOrWhiteSpace(request.AttestationResponse))
            return JsonResults.Error("invalid_request");

        // Find the pending setup credential. Must still be pending and still fresh — see TotpConfirmAsync.
        var setupCred = await mfaStore.GetCredentialAsync(userId, request.SetupToken, ct);
        if (setupCred is null || setupCred.Type != MfaCredentialType.WebAuthn || setupCred.PublicKeyJson is null
            || setupCred.Name != PendingWebAuthnName)
            return JsonResults.Error("invalid_setup_token");

        if (IsPendingExpired(setupCred))
        {
            await mfaStore.DeleteCredentialAsync(userId, setupCred.Id, ct);
            return JsonResults.Error("setup_expired");
        }

        var originalOptions = CredentialCreateOptions.FromJson(setupCred.PublicKeyJson);
        AuthenticatorAttestationRawResponse attestationResponse;
        try
        {
            attestationResponse = DeserializeFido2Attestation(request.AttestationResponse);
        }
        catch
        {
            return JsonResults.Error("invalid_attestation");
        }

        MfaCredential credential;
        try
        {
            credential = await webAuthnService.CompleteAttestationAsync(userId, originalOptions, attestationResponse, ct);
        }
        catch (Fido2VerificationException)
        {
            return JsonResults.Error("attestation_failed");
        }

        var credData = JsonSerializer.Deserialize(credential.PublicKeyJson!, AuthagonalJsonContext.Default.WebAuthnCredentialData);
        var credentialIdBytes = credData is not null ? Convert.FromBase64String(credData.CredentialId) : null;

        // Credential-ID uniqueness: reject a credential ID already registered to a DIFFERENT user, so
        // one user's passkey index entry can't be overwritten/hijacked by another's registration.
        if (credentialIdBytes is not null)
        {
            var existingOwner = await mfaStore.FindByWebAuthnCredentialIdAsync(credentialIdBytes, ct);
            if (existingOwner is { } owner && !string.Equals(owner.UserId, userId, StringComparison.Ordinal))
                return JsonResults.Error("credential_already_registered", 409);
        }

        // Delete the pending setup credential and create the real one
        await mfaStore.DeleteCredentialAsync(userId, request.SetupToken, ct);
        await mfaStore.CreateCredentialAsync(credential, ct);

        // Store WebAuthn credential ID mapping for discovery
        if (credentialIdBytes is not null)
        {
            await mfaStore.StoreWebAuthnCredentialIdMappingAsync(credentialIdBytes, userId, credential.Id, ct);
        }

        // Set MfaEnabled on user
        var user = await userStore.GetAsync(userId, ct);
        if (user is not null && !user.MfaEnabled)
        {
            user.MfaEnabled = true;
            user.UpdatedAt = DateTimeOffset.UtcNow;
            await userStore.UpdateAsync(user, ct);
        }

        if (user is not null)
            await authHooks.RunOnMfaEnrolledAsync(user.Id, user.Email, "webauthn", ct);

        // If authenticated via setup token, sign the cookie now. Run the onUserAuthenticated hook
        // BEFORE signing in, so an enforced hook that rejects the login prevents the cookie issue.
        if (setupChallenge is not null && user is not null)
        {
            await authHooks.RunOnUserAuthenticatedAsync(user.Id, user.Email, "password", setupChallenge.ClientId, ct);
            await CookieSignInHelper.SignInAsync(httpContext, user);
            await mfaStore.ConsumeChallengeAsync(setupChallenge.ChallengeId, ct);
        }

        return TypedResults.Json(new WebAuthnConfirmResponse { CredentialId = credential.Id }, AuthagonalJsonContext.Default.WebAuthnConfirmResponse);
    }

    private static async Task<IResult> RecoveryGenerateAsync(
        HttpContext httpContext,
        IMfaStore mfaStore,
        IUserStore userStore,
        RecoveryCodeService recoveryCodeService,
        ISecretProvider secretProvider,
        IEnumerable<IAuthHook> authHooks,
        CancellationToken ct)
    {
        var (userId, setupChallenge) = await ResolveUserIdAsync(httpContext, mfaStore, ct);
        if (userId is null) return Results.Unauthorized();

        // Regenerating look-up secrets is factor MANAGEMENT, not first-factor enrolment: it hands out ten
        // live bypass codes and destroys the user's existing ones. That needs a real session, same as
        // DeleteCredentialAsync below. The enrolment flow still reaches this — /totp/confirm and
        // /webauthn/confirm sign the cookie once the factor is proved, so the client calls this after.
        if (setupChallenge is not null)
            return JsonResults.Error("session_required", 403);

        // Must have a CONFIRMED TOTP or WebAuthn factor first. A pending (unconfirmed) enrolment row is
        // not a factor — treating it as one would let an abandoned setup attempt mint recovery codes.
        var existing = await mfaStore.GetCredentialsAsync(userId, ct);
        if (!existing.Any(c => (c.Type is MfaCredentialType.Totp or MfaCredentialType.WebAuthn) && IsConfirmed(c)))
            return JsonResults.Error("primary_method_required");

        // Delete existing recovery codes
        var oldRecoveryCodes = existing.Where(c => c.Type == MfaCredentialType.RecoveryCode).ToList();
        foreach (var old in oldRecoveryCodes)
            await mfaStore.DeleteCredentialAsync(userId, old.Id, ct);

        // Generate new codes. F35: the code hash is additionally encrypted at rest via the per-tenant
        // secret provider — same treatment as TOTP seeds (line ~150) — so a storage dump yields
        // `vault:` ciphertext, not an offline-brute-forceable unsalted hash that is itself an MFA bypass.
        var (plaintextCodes, credentials) = recoveryCodeService.Generate(userId);
        foreach (var cred in credentials)
        {
            // Name must be unique PER CODE: ISecretProvider treats `name` as the storage key, so ten codes
            // sharing one name meant each ProtectAsync overwrote the last. Nine of the ten codes then
            // resolved to the tenth's hash — nine were dead and one was accepted ten times, quietly turning
            // a ten-use recovery set into a single code. cred.Id is a fresh GUID per code.
            cred.SecretProtected = await secretProvider.ProtectAsync(
                $"mfa-recovery-{userId}-{cred.Id}", cred.SecretProtected!, ct);
            await mfaStore.CreateCredentialAsync(cred, ct);
        }

        var user = await userStore.GetAsync(userId, ct);
        await authHooks.RunOnRecoveryCodesRegeneratedAsync(userId, user?.Email ?? "", ct);

        return TypedResults.Json(new RecoveryCodesResponse { Codes = plaintextCodes.ToList() }, AuthagonalJsonContext.Default.RecoveryCodesResponse);
    }

    private static async Task<IResult> DeleteCredentialAsync(
        string credentialId,
        HttpContext httpContext,
        IMfaStore mfaStore,
        IUserStore userStore,
        IEnumerable<IAuthHook> authHooks,
        CancellationToken ct)
    {
        var (userId, setupChallenge) = await ResolveUserIdAsync(httpContext, mfaStore, ct);
        if (userId is null) return Results.Unauthorized();

        // Setup tokens exist only to add a FIRST factor. Removing/managing factors (which can
        // disable MFA) requires a real authenticated session — otherwise a leaked setup token could
        // downgrade a user's MFA.
        if (setupChallenge is not null)
            return JsonResults.Error("session_required", 403);

        var cred = await mfaStore.GetCredentialAsync(userId, credentialId, ct);
        if (cred is null)
            return JsonResults.Error("credential_not_found", 404);

        await mfaStore.DeleteCredentialAsync(userId, credentialId, ct);

        var user = await userStore.GetAsync(userId, ct);
        var mfaDisabled = false;

        // Check if user still has MFA credentials (excluding recovery codes)
        var remaining = await mfaStore.GetCredentialsAsync(userId, ct);
        if (!remaining.Any(c => c.Type is MfaCredentialType.Totp or MfaCredentialType.WebAuthn))
        {
            if (user is not null && user.MfaEnabled)
            {
                user.MfaEnabled = false;
                user.UpdatedAt = DateTimeOffset.UtcNow;
                await userStore.UpdateAsync(user, ct);
                mfaDisabled = true;
            }
        }

        await authHooks.RunOnMfaCredentialRemovedAsync(
            userId, user?.Email ?? "", cred.Type.ToString().ToLowerInvariant(), mfaDisabled, ct);

        return TypedResults.Json(new SuccessResponse(), AuthagonalJsonContext.Default.SuccessResponse);
    }

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Fido2 external type")]
    private static AuthenticatorAttestationRawResponse DeserializeFido2Attestation(string json)
        => JsonSerializer.Deserialize<AuthenticatorAttestationRawResponse>(json)!;
}

public sealed class TotpConfirmRequest
{
    public string? SetupToken { get; set; }
    public string? Code { get; set; }
}

public sealed class WebAuthnConfirmRequest
{
    public string? SetupToken { get; set; }
    public string? AttestationResponse { get; set; }
}
