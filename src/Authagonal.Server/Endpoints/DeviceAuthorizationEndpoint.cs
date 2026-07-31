using System.Security.Cryptography;
using System.Text.Json;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.Protocol.Services;
using Authagonal.Server.Services;

namespace Authagonal.Server.Endpoints;

public static class DeviceAuthorizationEndpoint
{
    public static IEndpointRouteBuilder MapDeviceAuthorizationEndpoints(this IEndpointRouteBuilder app)
    {
        // The URL the device tells the user to type. RFC 8628 §3.2 wants it short and typeable, so it
        // stays at the issuer root and redirects to wherever the login app actually lives.
        //
        // It previously resolved to nothing. The advertised verification_uri was hard-coded to
        // "{issuer}/device" while every other login-app URL in the server is built from
        // configuration["LoginAppUrl"], and the packaged app mounts BrowserRouter with
        // basename="/login" — so the code-entry page is at /login/device. There was no server route
        // for /device either, so the SPA fallback served the shell and a BrowserRouter whose basename
        // does not prefix the pathname rendered nothing at all. A user who typed the URL exactly as
        // instructed got a blank page, with no way to enter the code they were shown.
        app.MapGet("/device", (HttpContext httpContext, IConfiguration configuration) =>
        {
            var loginApp = (configuration["LoginAppUrl"] ?? "/login").TrimEnd('/');
            var userCode = httpContext.Request.Query["user_code"].FirstOrDefault();

            var target = $"{loginApp}/device";
            if (!string.IsNullOrWhiteSpace(userCode))
                target += $"?user_code={Uri.EscapeDataString(userCode)}";

            return Results.Redirect(target);
        })
        .AllowAnonymous()
        .WithTags("OAuth");

        // RFC 8628 §3.1 — Device Authorization Request
        app.MapPost("/connect/deviceauthorization", async (
            HttpContext httpContext,
            IClientStore clientStore,
            IGrantStore grantStore,
            ITenantContext tenantContext,
            IClientSecretVerifier secretVerifier,
            IRateLimiter rateLimiter,
            IConfiguration configuration,
            CancellationToken ct) =>
        {
            var form = await httpContext.Request.ReadFormAsync(ct);
            var scope = form["scope"].FirstOrDefault() ?? "openid";

            // Through the shared client-authentication path, like the token endpoint, PAR,
            // introspection and revocation.
            //
            // This endpoint read client_id and client_secret out of the form and nowhere else, so a
            // confidential client presenting HTTP Basic — the method discovery advertises first in
            // token_endpoint_auth_methods_supported and the default for most OAuth libraries — or a
            // private_key_jwt assertion was rejected with invalid_client at the one endpoint it could
            // not work around. The earlier fix here replaced an inline PasswordHasher comparison with
            // the injected IClientSecretVerifier but left the credential SOURCE alone, so the endpoint
            // still understood exactly one of the three registered methods. The shared path also
            // brings the client.Enabled refusal and the per-client throttle on secret verification.
            var (client, authError) = await Authagonal.Protocol.Endpoints.ClientAuthentication.AuthenticateAsync(
                httpContext, form, clientStore, secretVerifier,
                (error, description) => DeviceError(error, description), ct);
            if (authError is not null)
                return authError;

            var clientId = client!.ClientId;

            if (!client.AllowedGrantTypes.Contains("urn:ietf:params:oauth:grant-type:device_code", StringComparer.OrdinalIgnoreCase))
                return DeviceError("unauthorized_client", "Device authorization grant not allowed for this client");

            // Throttled per client, because this is an anonymous write.
            //
            // A device client may be public (RFC 8628's whole point is inputs-constrained devices that
            // cannot hold a secret), so reaching here needs only a client_id, and every accepted
            // request persists TWO grant rows — the device code and its user_code index. Unthrottled
            // that is a storage-flood primitive against the grant store, and it also burns user codes
            // out of a small alphabet. The budget is sized so a fleet of real devices provisioning at
            // once is unaffected.
            if (await rateLimiter.IsRateLimitedAsync($"device-auth|{clientId}", 120, TimeSpan.FromMinutes(1), ct))
                return DeviceError("temporarily_unavailable", "Too many device authorization requests");

            // Generate codes
            var deviceCode = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var userCode = GenerateUserCode();
            // Zero means the client was persisted before this field existed — use the 5-minute
            // default that Duende also ships with so behaviour stays predictable across imports.
            var expiresIn = client.DeviceCodeLifetimeSeconds > 0 ? client.DeviceCodeLifetimeSeconds : 300;

            var requestedScopes = scope.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var invalidScopes = requestedScopes.Except(client.AllowedScopes, StringComparer.OrdinalIgnoreCase).ToArray();
            if (invalidScopes.Length > 0)
                return DeviceError("invalid_scope", $"Scopes not allowed: {string.Join(", ", invalidScopes)}");
            var validScopes = requestedScopes.ToList();

            // Store as a persisted grant
            var data = JsonSerializer.Serialize(new DeviceCodeData
            {
                UserCode = userCode,
                ClientId = clientId,
                Scopes = validScopes,
                IsApproved = false,
                SubjectId = null,
            }, AuthagonalJsonContext.Default.DeviceCodeData);

            await grantStore.StoreAsync(new PersistedGrant
            {
                Key = $"device:{deviceCode}",
                Type = "device_code",
                ClientId = clientId,
                Data = data,
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn),
            }, ct);

            // Also index by user_code for the approval page. The key is the CANONICAL form — alphabet
            // characters only, no separator — because that is the only form both sides can agree on
            // once the user has retyped the code by hand (see NormalizeUserCode). The dash survives
            // only in what we hand back for display. Grants written under the older dashed key are
            // unreachable after this change; device codes expire in five minutes, so the window
            // closes on its own.
            await grantStore.StoreAsync(new PersistedGrant
            {
                Key = $"device_user:{NormalizeUserCode(userCode)}",
                Type = "device_user_code",
                ClientId = clientId,
                Data = deviceCode, // points back to the device code
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn),
            }, ct);

            var verificationUri = $"{tenantContext.Issuer}/device";

            return TypedResults.Json(new DeviceAuthorizationResponse
            {
                DeviceCode = deviceCode,
                UserCode = userCode,
                VerificationUri = verificationUri,
                VerificationUriComplete = $"{verificationUri}?user_code={userCode}",
                ExpiresIn = expiresIn,
            }, AuthagonalJsonContext.Default.DeviceAuthorizationResponse);
        })
        .AllowAnonymous()
        .DisableAntiforgery()
        .WithTags("OAuth");

        // What the user is actually being asked to approve. RFC 8628 §5.4 warns that the device flow's
        // remote-phishing shape depends on the user understanding the grant: the approval screen showed
        // nothing about the requesting client or the scopes, and `verification_uri_complete` pre-fills the
        // code, so approval was a single click on an opaque prompt. That is the illicit-consent-grant
        // pattern — an attacker starts a device flow, sends the victim the complete URI, and the victim
        // authorises the ATTACKER's device against their own account.
        //
        // Authenticated (the caller must be the person who would approve) and rate-limited on the SAME
        // bucket as the approval itself, so this cannot become an unauthenticated code-probing oracle or a
        // way to spend the approval budget for free.
        app.MapGet("/api/auth/device/info", async (
            HttpContext httpContext,
            IGrantStore grantStore,
            IClientStore clientStore,
            IUserStore userStore,
            IScopeRoleGate scopeRoleGate,
            IRateLimiter rateLimiter,
            CancellationToken ct) =>
        {
            if (httpContext.User.Identity?.IsAuthenticated != true)
                return JsonResults.Error("not_authenticated", 401);

            var subject = httpContext.User.FindFirst("sub")?.Value ?? "anonymous";
            if (await rateLimiter.IsRateLimitedAsync($"device-approve|{subject}", 10, TimeSpan.FromMinutes(1), ct))
                return JsonResults.Error("too_many_requests", 429);

            // Normalised the same way as the approve path — the grant key is stored separator-free,
            // so looking it up with the displayed XXXX-XXXX form finds nothing. This endpoint post-dates
            // the normalisation work and was missed by it.
            var userCode = NormalizeUserCode(httpContext.Request.Query["user_code"].FirstOrDefault());
            if (string.IsNullOrWhiteSpace(userCode))
                return TypedResults.Json(new ErrorInfoResponse { Error = "user_code_required" },
                    AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);

            var userCodeGrant = await grantStore.GetAsync($"device_user:{userCode}", ct);
            if (userCodeGrant is null || userCodeGrant.ConsumedAt is not null || userCodeGrant.ExpiresAt < DateTimeOffset.UtcNow)
                return TypedResults.Json(
                    new ErrorInfoResponse { Error = "invalid_user_code", Message = "Code is invalid or expired" },
                    AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);

            var deviceGrant = await grantStore.GetAsync($"device:{userCodeGrant.Data}", ct);
            if (deviceGrant is null || deviceGrant.ExpiresAt < DateTimeOffset.UtcNow)
                return TypedResults.Json(new ErrorInfoResponse { Error = "expired", Message = "Device code has expired" },
                    AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);

            var data = JsonSerializer.Deserialize(deviceGrant.Data, AuthagonalJsonContext.Default.DeviceCodeData)!;
            var client = await clientStore.GetAsync(data.ClientId, ct);

            // Show the scopes that would ACTUALLY be granted, after the per-user entitlement gate the
            // approval endpoint applies — displaying the raw request would overstate the grant.
            var subjectId = httpContext.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                ?? httpContext.User.FindFirst("sub")?.Value;
            var approver = subjectId is null ? null : await userStore.GetAsync(subjectId, ct);
            var entitled = await scopeRoleGate.FilterAsync(data.Scopes, approver?.Roles, ct);

            return TypedResults.Json(new DeviceInfoResponse
            {
                ClientId = data.ClientId,
                ClientName = client?.ClientName ?? data.ClientId,
                ClientUri = client?.ClientUri,
                LogoUri = client?.LogoUri,
                Scopes = [.. entitled],
            }, AuthagonalJsonContext.Default.DeviceInfoResponse);
        })
        .WithTags("OAuth");

        // User approval endpoint — called by the login app after authentication
        app.MapPost("/api/auth/device/approve", async (
            HttpContext httpContext,
            IGrantStore grantStore,
            IUserStore userStore,
            IScopeRoleGate scopeRoleGate,
            IRateLimiter rateLimiter,
            CancellationToken ct) =>
        {
            if (httpContext.User.Identity?.IsAuthenticated != true)
                return JsonResults.Error("not_authenticated", 401);

            // RFC 8628 §5.1: rate-limit user_code entry. A user_code is ~39 bits and this endpoint
            // already demands an authenticated session, so guessing is impractical rather than merely
            // slow — but an authenticated attacker grinding codes would otherwise be unbounded, and the
            // prize is a device approved in someone else's name.
            //
            // The default IRateLimiter is InProcessRateLimiter, which counts PER NODE: across N
            // replicas the real budget is 10/min x N, and the global limit is expected to come from an
            // edge rule outside this process. Pinned by DeviceApproval_ElevenWrongCodes_Returns429 —
            // this guard backs a documented security claim and must not be refactored away silently.
            var approverSubject = httpContext.User.FindFirst("sub")?.Value ?? "anonymous";
            if (await rateLimiter.IsRateLimitedAsync($"device-approve|{approverSubject}", 10, TimeSpan.FromMinutes(1), ct))
                return JsonResults.Error("too_many_requests", 429);

            var form = await httpContext.Request.ReadFormAsync(ct);
            var submittedUserCode = form["user_code"].FirstOrDefault();

            if (string.IsNullOrWhiteSpace(submittedUserCode))
                return TypedResults.Json(new ErrorInfoResponse { Error = "user_code_required" }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);

            // A submission that survives normalisation with nothing left held no code characters at
            // all — that's a bad code, not a missing field.
            var userCode = NormalizeUserCode(submittedUserCode);
            if (userCode.Length == 0)
                return TypedResults.Json(new ErrorInfoResponse { Error = "invalid_user_code", Message = "Code is invalid or expired" }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);

            // Look up the user code
            var userCodeGrant = await grantStore.GetAsync($"device_user:{userCode}", ct);
            if (userCodeGrant is null || userCodeGrant.ConsumedAt is not null || userCodeGrant.ExpiresAt < DateTimeOffset.UtcNow)
                return TypedResults.Json(new ErrorInfoResponse { Error = "invalid_user_code", Message = "Code is invalid or expired" }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);

            var deviceCode = userCodeGrant.Data;
            var deviceGrant = await grantStore.GetAsync($"device:{deviceCode}", ct);
            if (deviceGrant is null || deviceGrant.ExpiresAt < DateTimeOffset.UtcNow)
                return TypedResults.Json(new ErrorInfoResponse { Error = "expired", Message = "Device code has expired" }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);

            // A code that has already been redeemed must not be approved again. The write below is an
            // unconditional full-row upsert, and the row it writes carries ConsumedAt = null — so
            // approving a consumed code erased the marker the atomic consume relies on and handed the
            // device a second token set from one approval.
            if (deviceGrant.ConsumedAt is not null)
                return TypedResults.Json(
                    new ErrorInfoResponse { Error = "invalid_user_code", Message = "Code is invalid or expired" },
                    AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);

            // Approve — write the subject ID into the device code data
            var subjectId = httpContext.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                ?? httpContext.User.FindFirst("sub")?.Value;

            if (string.IsNullOrEmpty(subjectId))
                return JsonResults.Error("missing_identity", 401);

            var data = JsonSerializer.Deserialize(deviceGrant.Data, AuthagonalJsonContext.Default.DeviceCodeData)!;

            // Per-user scope entitlement (Scope.AllowedRoles). The device request itself is anonymous —
            // this approval is the first point the subject is known, which makes it the device flow's
            // equivalent of the check /connect/authorize does before consent. Without it this endpoint
            // would be a way around the gate.
            var approver = await userStore.GetAsync(subjectId, ct);
            var entitledScopes = await scopeRoleGate.FilterAsync(data.Scopes, approver?.Roles, ct);
            if (entitledScopes.Count == 0)
                return TypedResults.Json(
                    new ErrorInfoResponse { Error = "access_denied", Message = "You are not entitled to any of the requested scopes" },
                    AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 403);
            data.Scopes = [.. entitledScopes];

            data.IsApproved = true;
            data.SubjectId = subjectId;

            // GetAsync returns Key empty (the raw handle is never persisted — only its hash is the
            // partition key), so it MUST be re-set before the store re-hashes it; an empty key would
            // silently write the approval to the SHA-256("") partition and leave the device polling
            // authorization_pending forever.
            deviceGrant.Key = $"device:{deviceCode}";
            deviceGrant.Data = JsonSerializer.Serialize(data, AuthagonalJsonContext.Default.DeviceCodeData);
            deviceGrant.SubjectId = subjectId;
            await grantStore.StoreAsync(deviceGrant, ct);

            // Consume the user code so it can't be reused
            await grantStore.ConsumeAsync($"device_user:{userCode}", ct);

            return TypedResults.Json(new DeviceApprovedResponse(), AuthagonalJsonContext.Default.DeviceApprovedResponse);
        })
        .DisableAntiforgery()
        .WithTags("OAuth");

        return app;
    }

    /// <summary>
    /// The user_code alphabet — 8 characters drawn from these 31, so no ambiguous glyphs (0/O, 1/I/L)
    /// can survive being read off a TV screen and retyped on a phone.
    /// </summary>
    private const string UserCodeAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    private static string GenerateUserCode()
    {
        // GetInt32, not (byte % 31): 256 is not a multiple of 31, so the modulo drew the first eight
        // letters from nine byte values each and the rest from eight. A small bias, but it costs nothing
        // to not have one in the value standing between a stranger and an approved device.
        var code = new char[8];
        for (var i = 0; i < 8; i++)
            code[i] = UserCodeAlphabet[RandomNumberGenerator.GetInt32(UserCodeAlphabet.Length)];

        // Displayed with a separator (RFC 8628 §6.1 recommends chunking a code this long so it can be
        // read aloud and transcribed); the store key drops it again via NormalizeUserCode.
        return $"{new string(code, 0, 4)}-{new string(code, 4, 4)}";
    }

    /// <summary>
    /// Reduces a submitted user_code to the canonical form the grant store is keyed by: uppercase,
    /// alphabet characters only.
    /// </summary>
    /// <remarks>
    /// RFC 8628 §6.1 asks the server to strip punctuation, uppercase, and ignore characters outside
    /// the defined set. Only the uppercasing used to happen, so the dash we print was load-bearing:
    /// "WDJB-MJHT" worked while "WDJBMJHT", "WDJB MJHT" and "WDJB–MJHT" (an em dash, which is what a
    /// mobile keyboard's smart punctuation or a copy-paste out of a styled terminal produces) were
    /// all rejected as invalid codes. That is a user typing a perfectly good code and being told it
    /// is wrong — and worse, each variant they try spends one of the ten attempts per minute the
    /// brute-force limiter allows, so a user hunting for the format can lock themselves out of an
    /// approval that was valid the whole time.
    /// </remarks>
    private static string NormalizeUserCode(string? input)
    {
        if (string.IsNullOrEmpty(input))
            return string.Empty;

        var buffer = new char[input.Length];
        var length = 0;
        foreach (var ch in input)
        {
            var upper = char.ToUpperInvariant(ch);
            if (UserCodeAlphabet.Contains(upper))
                buffer[length++] = upper;
        }

        return new string(buffer, 0, length);
    }

    private static IResult DeviceError(string error, string description) =>
        // invalid_client is a 401, and RFC 6749 §5.2 requires a 401 from a client-authentication
        // failure to name the scheme in WWW-Authenticate — otherwise a device that guessed wrong
        // about how to authenticate has no way to learn what this server accepts.
        error == "invalid_client"
            ? JsonResults.UnauthorizedClient(error, description, realm: "deviceauthorization")
            : JsonResults.OAuthError(error, description,
                statusCode: error == "temporarily_unavailable" ? 429 : 400);
}

/// <summary>
/// What the device-approval screen must display before the user can approve: which application is asking,
/// and for what. Without this the user approved an opaque prompt.
/// </summary>
public sealed class DeviceInfoResponse
{
    public string ClientId { get; set; } = "";
    public string ClientName { get; set; } = "";
    public string? ClientUri { get; set; }
    public string? LogoUri { get; set; }
    public List<string> Scopes { get; set; } = [];
}

internal sealed class DeviceCodeData
{
    /// <summary>Advertised (and enforced) minimum poll interval, seconds — matches
    /// <c>DeviceAuthorizationResponse.Interval</c>. Polling faster earns a <c>slow_down</c> (RFC 8628 §3.5).</summary>
    public const int PollIntervalSeconds = 5;

    public required string UserCode { get; set; }
    public required string ClientId { get; set; }
    public required List<string> Scopes { get; set; }
    public bool IsApproved { get; set; }
    public string? SubjectId { get; set; }

    /// <summary>Timestamp of the last accepted token poll, for interval throttling. Null until first polled.</summary>
    /// <summary>
    /// No longer written. The RFC 8628 §3.5 poll interval is enforced through IRateLimiter keyed on
    /// the device code, because persisting it meant an unconditional row rewrite on every pending
    /// poll — which could erase a concurrent consume's marker. Kept so a grant serialized by an
    /// earlier version still deserializes.
    /// </summary>
    public DateTimeOffset? LastPolledAt { get; set; }
}
