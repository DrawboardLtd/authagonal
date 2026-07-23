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
                    GrantTypes.DeviceCode => await HandleDeviceCode(form, tokenService, grantStore, userStore, subjectResolver, client, ct),
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
        // Tracked per device_code via LastPolledAt; enforced regardless of approval state.
        var now = DateTimeOffset.UtcNow;
        if (data.LastPolledAt is { } lastPolled &&
            now - lastPolled < TimeSpan.FromSeconds(DeviceCodeData.PollIntervalSeconds))
        {
            return JsonResults.OAuthError("slow_down", "Polling too frequently. Increase your interval and try again.");
        }

        if (!data.IsApproved || string.IsNullOrEmpty(data.SubjectId))
        {
            // Record this poll so the next too-fast poll is throttled (best-effort — a lost update just
            // means one un-throttled poll). Only the pending path persists; an approved poll consumes.
            data.LastPolledAt = now;
            grant.Key = $"device:{deviceCode}";
            grant.Data = JsonSerializer.Serialize(data, AuthagonalJsonContext.Default.DeviceCodeData);
            await grantStore.StoreAsync(grant, ct);

            // RFC 8628 §3.5 — authorization_pending
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
