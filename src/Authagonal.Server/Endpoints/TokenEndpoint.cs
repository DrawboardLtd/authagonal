using System.Diagnostics;
using System.Text.Json;
using Authagonal.Core.Constants;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.Protocol.Endpoints;
using Authagonal.Protocol.Services;
using Authagonal.Server.Services;

namespace Authagonal.Server.Endpoints;

public static class TokenEndpoint
{
    public static IEndpointRouteBuilder MapTokenEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/connect/token", async (
            HttpContext httpContext,
            IProtocolTokenService tokenService,
            IClientStore clientStore,
            IUserStore userStore,
            IGrantStore grantStore,
            UserStoreOidcSubjectResolver subjectResolver,
            IClientSecretVerifier secretVerifier,
            IEnumerable<IAuthHook> authHooks,
            Authagonal.Core.Services.IRateLimiter rateLimiter,
            CancellationToken ct) =>
        {
            var form = await httpContext.Request.ReadFormAsync(ct);

            // Authenticate client
            var (client, authError) = await ClientAuthentication.AuthenticateAsync(
                httpContext, form, clientStore, secretVerifier, TokenGrantHandlers.TokenError, ct);
            if (authError is not null)
                return authError;

            var clientId = client!.ClientId;

            var grantType = form["grant_type"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(grantType))
                return TokenGrantHandlers.TokenError("invalid_request", "grant_type is required");

            if (!client.AllowedGrantTypes.Contains(grantType, StringComparer.OrdinalIgnoreCase))
                return TokenGrantHandlers.TokenError("unauthorized_client", "Grant type not allowed for this client");

            if (grantType is not (GrantTypes.AuthorizationCode or GrantTypes.RefreshToken or GrantTypes.ClientCredentials or GrantTypes.DeviceCode or GrantTypes.TokenExchange))
                return TokenGrantHandlers.TokenError("unsupported_grant_type", $"Grant type '{grantType}' is not supported");

            try
            {
                // Run the onTokenIssued hook BEFORE minting, so a rejection doesn't mint/persist
                // orphaned tokens (and never returns a token to the caller).
                await authHooks.RunOnTokenIssuedAsync(null, clientId, grantType, ct);

                var result = grantType switch
                {
                    GrantTypes.AuthorizationCode => await TokenGrantHandlers.HandleAuthorizationCode(form, tokenService, clientId, ct),
                    GrantTypes.RefreshToken => await TokenGrantHandlers.HandleRefreshToken(form, tokenService, clientId, ct),
                    GrantTypes.ClientCredentials => await TokenGrantHandlers.HandleClientCredentials(form, tokenService, clientId, ct),
                    GrantTypes.DeviceCode => await HandleDeviceCode(form, tokenService, grantStore, userStore, subjectResolver, rateLimiter, client, ct),
                    GrantTypes.TokenExchange => await TokenGrantHandlers.HandleTokenExchange(form, tokenService, clientId, ct),
                    _ => throw new UnreachableException()
                };

                return result;
            }
            catch (InvalidOperationException ex)
            {
                return TokenGrantHandlers.TokenError("invalid_grant", ex.Message);
            }
        })
        .AllowAnonymous()
        .DisableAntiforgery()
        .WithTags("OAuth");

        return app;
    }

    private static async Task<IResult> HandleDeviceCode(
        IFormCollection form,
        IProtocolTokenService tokenService,
        IGrantStore grantStore,
        IUserStore userStore,
        UserStoreOidcSubjectResolver subjectResolver,
        Authagonal.Core.Services.IRateLimiter rateLimiter,
        OAuthClient client,
        CancellationToken ct)
    {
        var deviceCode = form["device_code"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(deviceCode))
            return TokenGrantHandlers.TokenError("invalid_request", "device_code is required");

        var grant = await grantStore.GetAsync($"device:{deviceCode}", ct);
        if (grant is null)
            return TokenGrantHandlers.TokenError("invalid_grant", "Unknown device code");

        if (grant.ExpiresAt < DateTimeOffset.UtcNow)
        {
            await grantStore.RemoveAsync($"device:{deviceCode}", ct);
            return TokenGrantHandlers.TokenError("expired_token", "Device code has expired");
        }

        if (grant.ClientId != client.ClientId)
            return TokenGrantHandlers.TokenError("invalid_grant", "Device code was issued to a different client");

        if (grant.ConsumedAt is not null)
            return TokenGrantHandlers.TokenError("invalid_grant", "Device code has already been used");

        var data = JsonSerializer.Deserialize(grant.Data, AuthagonalJsonContext.Default.DeviceCodeData);
        if (data is null)
            return TokenGrantHandlers.TokenError("server_error", "Invalid device code data");

        // RFC 8628 §3.5 — throttle (F45): if the client polls faster than the advertised interval, tell
        // it to slow down instead of doing the work (the client must then add 5s to its interval).
        //
        // Through the rate limiter, keyed on the device code, rather than a LastPolledAt field written
        // back with StoreAsync. That write was an unconditional full-row upsert in every provider, and
        // the row it rewrote carries ConsumedAt = null — so a pending poll landing just after a
        // concurrent approved poll's TryMarkConsumedAsync erased the consumed marker that the atomic
        // consume depends on, and the device code became redeemable a second time. A poll counter is
        // not worth reopening the double-redemption the conditional consume exists to close, and the
        // limiter is the same mechanism user_code entry already throttles with.
        if (await rateLimiter.IsRateLimitedAsync(
                $"device-poll|{deviceCode}", 1, TimeSpan.FromSeconds(DeviceCodeData.PollIntervalSeconds), ct))
        {
            return JsonResults.OAuthError("slow_down", "Polling too frequently. Increase your interval and try again.");
        }

        // RFC 8628 §3.5 — access_denied. A refusal is an ANSWER, and the device has to be able to tell
        // it from silence: without this the only outcome of declining was authorization_pending until
        // expiry, so the device kept polling and the user's "no" changed nothing they could observe.
        // The code is consumed here rather than left to expire — the decision is final, and leaving a
        // denied code alive is a window in which the user could be talked into approving it after all.
        if (data.IsDenied)
        {
            await grantStore.RemoveAsync($"device:{deviceCode}", ct);
            return JsonResults.OAuthError("access_denied", "The user denied the request");
        }

        if (!data.IsApproved || string.IsNullOrEmpty(data.SubjectId))
        {
            // RFC 8628 §3.5 — authorization_pending. Nothing is written: the poll left no trace to
            // roll anything back with.
            return JsonResults.OAuthError("authorization_pending", "The user has not yet approved the request");
        }

        var user = await userStore.GetAsync(data.SubjectId, ct);
        if (user is null || !user.IsActive)
            return TokenGrantHandlers.TokenError("invalid_grant", "User not found or inactive");

        // Consume the device code atomically (F39): after approval, two polls could both pass the
        // ConsumedAt==null check above and both mint token sets. The ETag-conditional mark lets only
        // one win; the loser gets invalid_grant instead of a duplicate token set.
        grant.Key = $"device:{deviceCode}";
        grant.ConsumedAt = DateTimeOffset.UtcNow;
        if (!await grantStore.TryMarkConsumedAsync(grant, ct))
            return TokenGrantHandlers.TokenError("invalid_grant", "Device code has already been used");

        // Build the subject through the shared resolver so group inflation and claim
        // shape match what the authorize flow produces.
        var subject = await subjectResolver.BuildSubjectAsync(user, client, ct: ct);
        var response = await tokenService.HandleDeviceCodeAsync(subject, client, data.Scopes, ct);
        return Results.Ok(response);
    }
}
