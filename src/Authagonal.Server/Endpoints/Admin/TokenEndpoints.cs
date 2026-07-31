using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.Protocol.Services;
using Authagonal.Server.Services;

namespace Authagonal.Server.Endpoints.Admin;

public static class TokenEndpoints
{
    public static IEndpointRouteBuilder MapTokenAdminEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/token", CreateTokenForUser)
            .RequireAuthorization("IdentityAdmin")
            .WithTags("Admin - Tokens");

        return app;
    }

    /// <remarks>
    /// This mints a token AS another user, so it must clear every gate the ordinary mint paths do.
    /// It cleared none of them: it called <c>BuildSubjectAsync</c> — the raw builder — directly,
    /// while every other caller reaches it through <c>ResolveAsync</c>/<c>ResolveRefreshAsync</c>,
    /// which reject an inactive user first. So a deactivated account still produced a usable access
    /// token, even though <c>UpdateUser</c> deliberately revokes grants and rotates the security
    /// stamp on deactivation because "a disabled account that keeps working until its token expires
    /// has not been disabled". Likewise <c>client.Enabled</c> (enforced at authorize, client
    /// authentication and introspection), <see cref="IScopeRoleGate"/> (documented as applying on
    /// every path that mints a token for a human) and the <see cref="IAuthHook"/> pre-mint gate that
    /// /connect/token runs for every grant.
    /// </remarks>
    private static async Task<IResult> CreateTokenForUser(
        HttpContext httpContext,
        IProtocolTokenService tokenService,
        IClientStore clientStore,
        IUserStore userStore,
        UserStoreOidcSubjectResolver subjectResolver,
        IScopeRoleGate scopeRoleGate,
        IEnumerable<IAuthHook> authHooks,
        IConfiguration configuration,
        CancellationToken ct)
    {
        var query = httpContext.Request.Query;

        var clientId = query["clientId"].FirstOrDefault();
        var userId = query["userId"].FirstOrDefault();
        var scopesParam = query["scopes"].FirstOrDefault();

        if (string.IsNullOrWhiteSpace(clientId))
            return Results.BadRequest(new { error = "invalid_request", error_description = "clientId query parameter is required" });

        if (string.IsNullOrWhiteSpace(userId))
            return Results.BadRequest(new { error = "invalid_request", error_description = "userId query parameter is required" });

        var client = await clientStore.GetAsync(clientId, ct);
        if (client is null)
            return Results.NotFound(new { error = "client_not_found", error_description = $"Client '{clientId}' not found" });

        if (!client.Enabled)
            return Results.Json(new { error = "unauthorized_client", error_description = $"Client '{clientId}' is disabled" }, statusCode: 403);

        var user = await userStore.GetAsync(userId, ct);
        if (user is null)
            return Results.NotFound(new { error = "user_not_found", error_description = $"User '{userId}' not found" });

        // Deactivation must mean the same thing here as everywhere else.
        if (!user.IsActive)
            return Results.Json(new { error = "user_inactive", error_description = $"User '{userId}' is deactivated" }, statusCode: 403);

        var scopes = string.IsNullOrWhiteSpace(scopesParam)
            ? client.AllowedScopes
            : scopesParam.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

        // Constrain to the client's registered scopes — this endpoint must not mint scopes the
        // client itself couldn't request.
        var disallowed = scopes.Except(client.AllowedScopes, StringComparer.OrdinalIgnoreCase).ToArray();
        if (disallowed.Length > 0)
            return Results.BadRequest(new { error = "invalid_scope", error_description = $"Scopes not allowed for client '{clientId}': {string.Join(", ", disallowed)}" });

        // Never issue the admin scope through this impersonation endpoint — otherwise a (possibly
        // time-limited) admin token can mint a long-lived admin access+refresh token, defeating
        // rotation/revocation.
        //
        // Through AdminScopeReservation.Grants, which splits each entry on whitespace, rather than the
        // whole-element Contains this used to do. A stored AllowedScopes entry is not necessarily one
        // scope: with no `scopes` parameter this endpoint defaults to client.AllowedScopes verbatim, so
        // an entry "openid authagonal-admin" was one opaque string here and two scopes in the emitted
        // claim. Every sibling guard was moved to the split comparison; this one was left behind.
        var adminScope = configuration["AdminApi:Scope"] ?? AdminScopeReservation.DefaultAdminScope;
        if (AdminScopeReservation.Grants(scopes, adminScope))
            return Results.Json(new { error = "forbidden_scope", error_description = $"The '{adminScope}' scope cannot be issued via this endpoint" }, statusCode: 403);

        // Role-gated scopes are entitlements of the impersonated user, not of the admin doing the
        // impersonating. Without this the endpoint handed out scopes the target holds no role for —
        // on the one path where the caller chooses both the user and the scopes.
        scopes = (await scopeRoleGate.FilterAsync(scopes, user.Roles, ct)).ToList();

        var subject = await subjectResolver.BuildSubjectAsync(user, client, ct: ct);

        // The documented "throw to reject the token issuance" gate. Impersonation is the issuance a
        // host most needs to see, and it was the one issuance the hooks never heard about.
        await authHooks.RunOnTokenIssuedAsync(user.Id, client.ClientId, "admin_mint", ct);

        var accessToken = await tokenService.CreateAccessTokenAsync(subject, client, scopes, ct: ct);
        // Refresh token only when offline access was actually requested and the client allows it —
        // an unconditional mint handed every impersonation call a long-lived credential, the exact
        // persistence the admin-scope guard above exists to prevent.
        string? refreshToken = null;
        if (scopes.Contains("offline_access", StringComparer.OrdinalIgnoreCase) && client.AllowOfflineAccess)
            refreshToken = await tokenService.CreateRefreshTokenAsync(subject, client, scopes, ct: ct);

        string? idToken = null;
        if (scopes.Contains("openid", StringComparer.OrdinalIgnoreCase))
        {
            idToken = await tokenService.CreateIdTokenAsync(subject, client, scopes, ct: ct);
        }

        // RFC 6749 §5.1 — the lifetime of the token actually minted. The mint clamps exp down to the
        // subject's federated session cap; reporting the client's configured ceiling here told the
        // caller the token lived longer than it does.
        var mintedAt = DateTimeOffset.UtcNow;
        var effectiveExpiry = Authagonal.Protocol.Services.ProtocolTokenService.EffectiveAccessTokenExpiry(
            subject, client, null, mintedAt);

        var response = new TokenResponse
        {
            AccessToken = accessToken,
            ExpiresIn = Math.Max(0, (int)Math.Round((effectiveExpiry - mintedAt).TotalSeconds)),
            RefreshToken = refreshToken,
            IdToken = idToken,
            Scope = string.Join(' ', scopes)
        };

        return Results.Ok(response);
    }
}
