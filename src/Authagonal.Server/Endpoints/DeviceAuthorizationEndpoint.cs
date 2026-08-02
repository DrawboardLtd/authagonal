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
            //
            // The OTHER bound this endpoint needs — one on client-secret verification, so a junk secret
            // cannot buy an unmetered 600k-iteration PBKDF2 — is not repeated here: it comes with
            // ClientAuthentication.AuthenticateAsync above, on the same `client-secret|{id}` budget key
            // as /connect/token, so an attacker cannot get a fresh budget by switching endpoints. That
            // is the point of routing through the shared path rather than re-implementing verification.
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

            // no-store: this body IS the credential pair. device_code redeems for tokens and user_code is
            // what the user types to approve, so an intermediary caching it hands the next caller a live
            // authorization. RFC 8628 does not restate RFC 6749 §5.1 here, but the reason for that rule
            // applies unchanged.
            return JsonResults.NoStore(TypedResults.Json(new DeviceAuthorizationResponse
            {
                DeviceCode = deviceCode,
                UserCode = userCode,
                VerificationUri = verificationUri,
                VerificationUriComplete = $"{verificationUri}?user_code={userCode}",
                ExpiresIn = expiresIn,
            }, AuthagonalJsonContext.Default.DeviceAuthorizationResponse));
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

        // RFC 8628 §3.5 access_denied — the user's "no".
        //
        // There was no server-side deny at all: the approval screen's Cancel button only cleared local
        // state, DeviceCodeData had nowhere to record a refusal, and the token endpoint could only emit
        // authorization_pending / slow_down / expired_token / invalid_grant. So a user who declined left
        // the device polling as though they had not answered yet, until the code expired — the device
        // could not tell "not yet" from "no", and the RFC's own signal for the decision the user actually
        // made was unreachable. Worse for the illicit-consent case §5.4 warns about: a victim who
        // realises what the prompt is and cancels leaves the attacker's device polling for the full
        // remaining lifetime, in case they change their mind.
        app.MapPost("/api/auth/device/deny", async (
            HttpContext httpContext,
            IGrantStore grantStore,
            IRateLimiter rateLimiter,
            CancellationToken ct) =>
        {
            if (httpContext.User.Identity?.IsAuthenticated != true)
                return JsonResults.Error("not_authenticated", 401);

            // The third consent-granting interactive POST in this server, and the only one that did not
            // carry this guard — ApprovalEndpoints and AgentConsentEndpoints both do, for exactly this
            // threat. Cookie-only authentication, .DisableAntiforgery() below, and SameSite=Lax does NOT
            // withhold the session cookie from a same-site CROSS-ORIGIN POST: on idp.acme.com beside
            // app.acme.com — the normal deployment shape — any XSS or hostile script on a sibling origin
            // could fetch() this endpoint with credentials:'include' and a user_code it chose, approving
            // its own device as the visiting user and leaving a consent grant recorded in their name.
            //
            // Ahead of the rate limiter deliberately: a cross-origin script must not be able to spend the
            // victim's device-approval budget on its way to being refused.
            if (Services.InteractiveOriginGuard.Check(httpContext) is { } originError)
                return originError;

            // The same budget as approve and info: refusing is also a code submission, so it must not be
            // a way to probe user codes on a fresh allowance.
            var denierSubject = httpContext.User.FindFirst("sub")?.Value ?? "anonymous";
            if (await rateLimiter.IsRateLimitedAsync($"device-approve|{denierSubject}", 10, TimeSpan.FromMinutes(1), ct))
                return JsonResults.Error("too_many_requests", 429);

            var denyForm = await httpContext.Request.ReadFormAsync(ct);
            var userCode = NormalizeUserCode(denyForm["user_code"].FirstOrDefault());
            if (userCode.Length == 0)
                return TypedResults.Json(new ErrorInfoResponse { Error = "user_code_required" },
                    AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);

            var userCodeGrant = await grantStore.GetAsync($"device_user:{userCode}", ct);
            if (userCodeGrant is null || userCodeGrant.ConsumedAt is not null || userCodeGrant.ExpiresAt < DateTimeOffset.UtcNow)
                return TypedResults.Json(
                    new ErrorInfoResponse { Error = "invalid_user_code", Message = "Code is invalid or expired" },
                    AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);

            var deniedDeviceCode = userCodeGrant.Data;
            var deniedGrant = await grantStore.GetAsync($"device:{deniedDeviceCode}", ct);
            if (deniedGrant is null || deniedGrant.ExpiresAt < DateTimeOffset.UtcNow)
                return TypedResults.Json(new ErrorInfoResponse { Error = "expired", Message = "Device code has expired" },
                    AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);

            // An already-redeemed code cannot be retracted, and rewriting the row would erase the
            // consumed marker the atomic consume depends on — the same trap the approve path carries.
            if (deniedGrant.ConsumedAt is not null)
                return TypedResults.Json(
                    new ErrorInfoResponse { Error = "invalid_user_code", Message = "Code is invalid or expired" },
                    AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);

            // Claim the user_code ATOMICALLY, before the write — the same order, and for the same reason,
            // as the approve path above.
            //
            // Everything above this line is check-then-act, so a racing pair of requests both read the
            // user-code grant un-consumed and both read the device grant un-consumed. The write below is
            // an unconditional full-row upsert, and the row it serialises carries whatever ConsumedAt it
            // read — so a deny that lands AFTER the device polled and the token endpoint marked the code
            // spent would erase that marker and re-arm the device code for a second token set. Consuming
            // afterwards, as this did, does not help: by then the damaging write has already happened.
            //
            // TryConsumeAsync is the conditional (ETag) delete, so exactly one caller wins and the losers
            // stop here having written nothing. That also settles deny-racing-approve: whichever claims
            // the code decides, and the other cannot overwrite the decision.
            if (!await grantStore.TryConsumeAsync($"device_user:{userCode}", ct))
                return TypedResults.Json(
                    new ErrorInfoResponse { Error = "invalid_user_code", Message = "Code is invalid or expired" },
                    AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);

            var deniedData = JsonSerializer.Deserialize(deniedGrant.Data, AuthagonalJsonContext.Default.DeviceCodeData)!;
            deniedData.IsDenied = true;
            deniedData.IsApproved = false;
            deniedData.SubjectId = null;

            // GetAsync returns Key empty (only the hash is persisted), so it must be re-set or the write
            // lands in the SHA-256("") partition — see the approve path.
            deniedGrant.Key = $"device:{deniedDeviceCode}";
            deniedGrant.Data = JsonSerializer.Serialize(deniedData, AuthagonalJsonContext.Default.DeviceCodeData);
            await grantStore.StoreAsync(deniedGrant, ct);

            return TypedResults.Json(new SuccessResponse { Success = true }, AuthagonalJsonContext.Default.SuccessResponse);
        })
        .DisableAntiforgery()
        .WithTags("OAuth");

        // User approval endpoint — called by the login app after authentication
        app.MapPost("/api/auth/device/approve", async (
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

            // The third consent-granting interactive POST in this server, and the only one that did not
            // carry this guard — ApprovalEndpoints and AgentConsentEndpoints both do, for exactly this
            // threat. Cookie-only authentication, .DisableAntiforgery() below, and SameSite=Lax does NOT
            // withhold the session cookie from a same-site CROSS-ORIGIN POST: on idp.acme.com beside
            // app.acme.com — the normal deployment shape — any XSS or hostile script on a sibling origin
            // could fetch() this endpoint with credentials:'include' and a user_code it chose, approving
            // its own device as the visiting user and leaving a consent grant recorded in their name.
            //
            // Ahead of the rate limiter deliberately: a cross-origin script must not be able to spend the
            // victim's device-approval budget on its way to being refused.
            if (Services.InteractiveOriginGuard.Check(httpContext) is { } originError)
                return originError;

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
            // device a second token set from one approval. This read is the cheap first filter; the
            // atomic claim below is what actually holds under concurrency.
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

            // Per-scope narrowing, as /connect/authorize's consent screen does. Device approval was
            // all-or-nothing over whatever the device asked for: the only choices were "grant everything"
            // or "say nothing and let it expire". A submitted set only ever narrows — it is intersected
            // with the entitled set, never unioned — so a tampered body cannot widen the grant.
            var selected = (form["scopes"].FirstOrDefault() ?? "")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var grantedScopes = selected.Length == 0
                ? [.. entitledScopes]
                : entitledScopes.Where(s => selected.Contains(s, StringComparer.Ordinal)).ToList();
            if (grantedScopes.Count == 0)
                return TypedResults.Json(
                    new ErrorInfoResponse { Error = "invalid_scope", Message = "Select at least one permission, or cancel to deny the request" },
                    AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);
            data.Scopes = grantedScopes;

            data.IsApproved = true;
            data.SubjectId = subjectId;

            // Claim the user_code ATOMICALLY, and do it before the approval write rather than after it.
            //
            // Everything above this line is check-then-act: two overlapping approvals of one user_code
            // both read the user-code grant un-consumed and both read the device grant un-consumed (the
            // budget is 10/min, so a racing pair is free). The old order let both of them through, and
            // the loser's StoreAsync — an unconditional full-row upsert whose serialized row carries
            // ConsumedAt = null — could land AFTER the device had polled and TryMarkConsumedAsync had
            // marked the device code spent. That erased the consumed marker and re-armed the device code
            // for a second token set from a single approval.
            //
            // TryConsumeAsync is the conditional (ETag) delete, so exactly one caller can win the claim;
            // the loser stops here having written nothing. It runs after the entitlement gate so a
            // refusal does not burn a code the user is still entitled to retry with.
            if (!await grantStore.TryConsumeAsync($"device_user:{userCode}", ct))
                return TypedResults.Json(
                    new ErrorInfoResponse { Error = "invalid_user_code", Message = "Code is invalid or expired" },
                    AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);

            // GetAsync returns Key empty (the raw handle is never persisted — only its hash is the
            // partition key), so it MUST be re-set before the store re-hashes it; an empty key would
            // silently write the approval to the SHA-256("") partition and leave the device polling
            // authorization_pending forever.
            deviceGrant.Key = $"device:{deviceCode}";
            deviceGrant.Data = JsonSerializer.Serialize(data, AuthagonalJsonContext.Default.DeviceCodeData);
            deviceGrant.SubjectId = subjectId;
            await grantStore.StoreAsync(deviceGrant, ct);

            // Record it as consent, in the same shape /connect/authorize records.
            //
            // client.RequireConsent was read at exactly one site — the authorize endpoint — so the device
            // grant was the one interactive path that could hand a client scopes without producing a
            // consent record. Two consequences: the approval never appeared on the Authorized Apps page,
            // so the user had no way to see or revoke what their television was holding; and a client
            // registered with RequireConsent got, through this path, a grant it would have had to ask for
            // on any other. This screen IS the consent interaction for the device grant — it shows the
            // client and the scopes and the user chooses them — so what it records is a real consent,
            // narrowed to what was actually approved rather than what was requested.
            var deviceClient = await clientStore.GetAsync(data.ClientId, ct);
            if (deviceClient is not null)
            {
                var consentKey = $"consent:{subjectId}:{data.ClientId}";
                var priorConsent = await grantStore.GetAsync(consentKey, ct);
                var priorScopes = priorConsent is null
                    ? []
                    : JsonSerializer.Deserialize(priorConsent.Data, AuthagonalJsonContext.Default.ConsentData)?.Scopes ?? [];

                var consentData = new AuthorizeEndpoint.ConsentData
                {
                    // Additive: approving a device must not silently retract consent the user gave the
                    // same client through a browser flow.
                    Scopes = [.. priorScopes.Union(grantedScopes, StringComparer.Ordinal)],
                    // What the user was shown. Anything offered and NOT selected is recorded so a later
                    // request for it prompts once rather than on every authorize.
                    OfferedScopes = [.. priorScopes.Union(entitledScopes, StringComparer.Ordinal)],
                    ConsentedAt = DateTimeOffset.UtcNow,
                };

                await grantStore.StoreAsync(new PersistedGrant
                {
                    Key = consentKey,
                    Type = Core.Constants.PersistedGrantTypes.Consent,
                    SubjectId = subjectId,
                    ClientId = data.ClientId,
                    Data = JsonSerializer.Serialize(consentData, AuthagonalJsonContext.Default.ConsentData),
                    CreatedAt = DateTimeOffset.UtcNow,
                    ExpiresAt = DateTimeOffset.UtcNow.AddYears(5),
                }, ct);
            }

            // No ConsumeAsync here: the user_code was already CLAIMED above, atomically, before the
            // approval write. Consuming it again at the end would be the old order — the one where the
            // loser of a racing pair could land its full-row upsert after the device had polled and
            // re-arm the device code for a second token set.
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

    /// <summary>
    /// The user said no. RFC 8628 §3.5 <c>access_denied</c>: the device must be told the decision was
    /// made and was negative, rather than polling <c>authorization_pending</c> until the code expires.
    /// </summary>
    public bool IsDenied { get; set; }

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
