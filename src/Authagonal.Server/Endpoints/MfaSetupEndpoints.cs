using System.Security.Claims;
using System.Text.Json;
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

        return (challenge.UserId, challenge);
    }

    private static async Task<IResult> GetStatusAsync(
        HttpContext httpContext,
        IMfaStore mfaStore,
        CancellationToken ct)
    {
        var (userId, _) = await ResolveUserIdAsync(httpContext, mfaStore, ct);
        if (userId is null) return Results.Unauthorized();

        var credentials = await mfaStore.GetCredentialsAsync(userId, ct);

        // Exclude pending setup credentials from status
        var confirmed = credentials
            .Where(c => c.Name is not "TOTP (pending)" and not "WebAuthn (pending)")
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

        return TypedResults.Json(new MfaStatusResponse { Enabled = enabled, Methods = methods }, AuthagonalJsonContext.Default.MfaStatusResponse);
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
            .Where(c => c.Type == MfaCredentialType.Totp && c.Name == "TOTP (pending)")
            .ToList();
        foreach (var pending in pendingTotp)
            await mfaStore.DeleteCredentialAsync(userId, pending.Id, ct);

        if (credentials.Any(c => c.Type == MfaCredentialType.Totp && c.Name != "TOTP (pending)"))
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
            Name = "TOTP (pending)",
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
        IEnumerable<IAuthHook> authHooks,
        CancellationToken ct)
    {
        var (userId, setupChallenge) = await ResolveUserIdAsync(httpContext, mfaStore, ct);
        if (userId is null) return Results.Unauthorized();

        if (string.IsNullOrWhiteSpace(request.SetupToken) || string.IsNullOrWhiteSpace(request.Code))
            return JsonResults.Error("invalid_request");

        // Find the pending credential
        var cred = await mfaStore.GetCredentialAsync(userId, request.SetupToken, ct);
        if (cred is null || cred.Type != MfaCredentialType.Totp)
            return JsonResults.Error("invalid_setup_token");

        // Verify code against the stored secret
        var secretBase64 = await secretProvider.ResolveAsync(cred.SecretProtected!, ct);
        var secret = Convert.FromBase64String(secretBase64);

        if (!totpService.VerifyCode(secret, request.Code))
            return JsonResults.Error("invalid_code");

        // Confirm: update the credential name to indicate it's active
        cred.Name = "Authenticator app";
        await mfaStore.UpdateCredentialAsync(cred, ct);

        // Set MfaEnabled on user
        var user = await userStore.GetAsync(userId, ct);
        if (user is not null && !user.MfaEnabled)
        {
            user.MfaEnabled = true;
            user.UpdatedAt = DateTimeOffset.UtcNow;
            await userStore.UpdateAsync(user, ct);
        }

        // If authenticated via setup token, sign the cookie now (user proved password + TOTP)
        if (setupChallenge is not null && user is not null)
        {
            await CookieSignInHelper.SignInAsync(httpContext, user);
            await mfaStore.ConsumeChallengeAsync(setupChallenge.ChallengeId, ct);
            await authHooks.RunOnUserAuthenticatedAsync(user.Id, user.Email, "password", setupChallenge.ClientId, ct);
        }

        return TypedResults.Json(new SuccessResponse(), AuthagonalJsonContext.Default.SuccessResponse);
    }

    private static async Task<IResult> WebAuthnSetupAsync(
        HttpContext httpContext,
        IMfaStore mfaStore,
        IUserStore userStore,
        WebAuthnService webAuthnService,
        CancellationToken ct)
    {
        var (userId, _) = await ResolveUserIdAsync(httpContext, mfaStore, ct);
        if (userId is null) return Results.Unauthorized();

        var user = await userStore.GetAsync(userId, ct);
        if (user is null) return Results.Unauthorized();

        var existingCredentials = await mfaStore.GetCredentialsAsync(userId, ct);

        // Clean up any orphaned pending WebAuthn setup credentials (from cancelled attempts)
        var pendingWebAuthn = existingCredentials
            .Where(c => c.Type == MfaCredentialType.WebAuthn && c.Name == "WebAuthn (pending)")
            .ToList();
        foreach (var pending in pendingWebAuthn)
            await mfaStore.DeleteCredentialAsync(userId, pending.Id, ct);

        // Re-fetch if we cleaned up any
        if (pendingWebAuthn.Count > 0)
            existingCredentials = await mfaStore.GetCredentialsAsync(userId, ct);

        var (options, setupToken) = webAuthnService.CreateAttestationOptions(user, existingCredentials);

        // Store the options JSON temporarily in a credential so we can verify later
        var setupCred = new MfaCredential
        {
            Id = setupToken,
            UserId = userId,
            Type = MfaCredentialType.WebAuthn,
            Name = "WebAuthn (pending)",
            PublicKeyJson = options.ToJson(),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await mfaStore.CreateCredentialAsync(setupCred, ct);

        return TypedResults.Json(new WebAuthnSetupResponse { SetupToken = setupToken, Options = options }, AuthagonalJsonContext.Default.WebAuthnSetupResponse);
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

        // Find the pending setup credential
        var setupCred = await mfaStore.GetCredentialAsync(userId, request.SetupToken, ct);
        if (setupCred is null || setupCred.Type != MfaCredentialType.WebAuthn || setupCred.PublicKeyJson is null)
            return JsonResults.Error("invalid_setup_token");

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

        // Delete the pending setup credential and create the real one
        await mfaStore.DeleteCredentialAsync(userId, request.SetupToken, ct);
        await mfaStore.CreateCredentialAsync(credential, ct);

        // Store WebAuthn credential ID mapping for discovery
        var credData = JsonSerializer.Deserialize(credential.PublicKeyJson!, AuthagonalJsonContext.Default.WebAuthnCredentialData);
        if (credData is not null)
        {
            await mfaStore.StoreWebAuthnCredentialIdMappingAsync(
                Convert.FromBase64String(credData.CredentialId), userId, credential.Id, ct);
        }

        // Set MfaEnabled on user
        var user = await userStore.GetAsync(userId, ct);
        if (user is not null && !user.MfaEnabled)
        {
            user.MfaEnabled = true;
            user.UpdatedAt = DateTimeOffset.UtcNow;
            await userStore.UpdateAsync(user, ct);
        }

        // If authenticated via setup token, sign the cookie now
        if (setupChallenge is not null && user is not null)
        {
            await CookieSignInHelper.SignInAsync(httpContext, user);
            await mfaStore.ConsumeChallengeAsync(setupChallenge.ChallengeId, ct);
            await authHooks.RunOnUserAuthenticatedAsync(user.Id, user.Email, "password", setupChallenge.ClientId, ct);
        }

        return TypedResults.Json(new WebAuthnConfirmResponse { CredentialId = credential.Id }, AuthagonalJsonContext.Default.WebAuthnConfirmResponse);
    }

    private static async Task<IResult> RecoveryGenerateAsync(
        HttpContext httpContext,
        IMfaStore mfaStore,
        IUserStore userStore,
        RecoveryCodeService recoveryCodeService,
        CancellationToken ct)
    {
        var (userId, _) = await ResolveUserIdAsync(httpContext, mfaStore, ct);
        if (userId is null) return Results.Unauthorized();

        // Must have TOTP or WebAuthn enrolled first
        var existing = await mfaStore.GetCredentialsAsync(userId, ct);
        if (!existing.Any(c => c.Type is MfaCredentialType.Totp or MfaCredentialType.WebAuthn))
            return JsonResults.Error("primary_method_required");

        // Delete existing recovery codes
        var oldRecoveryCodes = existing.Where(c => c.Type == MfaCredentialType.RecoveryCode).ToList();
        foreach (var old in oldRecoveryCodes)
            await mfaStore.DeleteCredentialAsync(userId, old.Id, ct);

        // Generate new codes
        var (plaintextCodes, credentials) = recoveryCodeService.Generate(userId);
        foreach (var cred in credentials)
            await mfaStore.CreateCredentialAsync(cred, ct);

        return TypedResults.Json(new RecoveryCodesResponse { Codes = plaintextCodes.ToList() }, AuthagonalJsonContext.Default.RecoveryCodesResponse);
    }

    private static async Task<IResult> DeleteCredentialAsync(
        string credentialId,
        HttpContext httpContext,
        IMfaStore mfaStore,
        IUserStore userStore,
        CancellationToken ct)
    {
        var (userId, _) = await ResolveUserIdAsync(httpContext, mfaStore, ct);
        if (userId is null) return Results.Unauthorized();

        var cred = await mfaStore.GetCredentialAsync(userId, credentialId, ct);
        if (cred is null)
            return JsonResults.Error("credential_not_found", 404);

        await mfaStore.DeleteCredentialAsync(userId, credentialId, ct);

        // Check if user still has MFA credentials (excluding recovery codes)
        var remaining = await mfaStore.GetCredentialsAsync(userId, ct);
        if (!remaining.Any(c => c.Type is MfaCredentialType.Totp or MfaCredentialType.WebAuthn))
        {
            var user = await userStore.GetAsync(userId, ct);
            if (user is not null && user.MfaEnabled)
            {
                user.MfaEnabled = false;
                user.UpdatedAt = DateTimeOffset.UtcNow;
                await userStore.UpdateAsync(user, ct);
            }
        }

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
