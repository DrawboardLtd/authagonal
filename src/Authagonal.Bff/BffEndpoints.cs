using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Authagonal.Bff;

internal static class BffEndpoints
{
    private const string CorrelationPurpose = "agbff-correlation-v1";

    // id_token claims that are protocol machinery, not user profile — never surfaced to the SPA.
    private static readonly HashSet<string> ProtocolClaims = new(StringComparer.Ordinal)
    {
        "iss", "aud", "exp", "iat", "nbf", "nonce", "at_hash", "c_hash", "s_hash",
        "azp", "jti", "sid", "auth_time", "acr", "amr", "typ",
    };

    public static async Task<IResult> LoginAsync(
        HttpContext ctx,
        string? returnUrl,
        IOptions<AuthagonalBffOptions> options,
        BffOidcConfig oidc,
        IBffTenantResolver tenants,
        ICookieProtector protector,
        CancellationToken ct)
    {
        var o = options.Value;

        // Which tenant is this login for? In single-tenant mode the key is null; in multi-tenant mode it's the
        // configured query parameter (e.g. ?slug=acme). The resolver turns it into the tenant's client config.
        var tenantKey = o.IsMultiTenant ? ctx.Request.Query[o.TenantQueryParam!].ToString() : null;
        var tenant = await tenants.ResolveAsync(tenantKey, ct);
        if (tenant is null)
            return Fail("unknown_tenant");

        var config = await oidc.GetAsync(tenant.Authority, ct);

        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var nonce = Base64Url(RandomNumberGenerator.GetBytes(32));
        var codeVerifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var codeChallenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));
        var safeReturn = SanitizeReturnUrl(returnUrl, o);
        var redirectUri = BuildRedirectUri(ctx, o);

        var correlation = new CorrelationState(state, codeVerifier, nonce, safeReturn, tenant.TenantKey);
        var payload = protector.Protect(
            JsonSerializer.Serialize(correlation, BffJsonContext.Default.CorrelationState), CorrelationPurpose);
        // Per-login cookie (suffixed with the state) so concurrent logins can't clobber each other.
        // With a single shared cookie, any /bff/login started while another is mid-flight (another
        // tab's 401→login restart; a user pausing on the IdP's interstitial page) overwrote the
        // first flow's correlation, and the completing callback died with state_mismatch. State is
        // self-generated base64url, so it is cookie-name-safe.
        ctx.Response.Cookies.Append(CorrelationCookieFor(o, state), payload, TransientCookieOptions(ctx, o));

        var authorizeParams = new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = tenant.ClientId,
            ["redirect_uri"] = redirectUri,
            ["scope"] = tenant.ScopeString,
            ["state"] = state,
            ["nonce"] = nonce,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
        };
        // Forward consumer-allowlisted params (e.g. idp_hint + a share-link token) so IdP-federation flows the
        // OIDC client itself doesn't model can be driven through the BFF. Standard params above always win.
        foreach (var name in o.LoginPassthroughParams)
        {
            var value = ctx.Request.Query[name].ToString();
            if (!string.IsNullOrEmpty(value) && !authorizeParams.ContainsKey(name))
                authorizeParams[name] = value;
        }
        var authorizeUrl = QueryHelpers.AddQueryString(config.AuthorizationEndpoint, authorizeParams);
        return Results.Redirect(authorizeUrl);
    }

    public static async Task<IResult> CallbackAsync(
        HttpContext ctx,
        string? code,
        string? state,
        string? error,
        IOptions<AuthagonalBffOptions> options,
        BffOidcConfig oidc,
        ITokenClient tokens,
        IBffTenantResolver tenants,
        IBffSessionStore store,
        ICookieProtector protector,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var o = options.Value;
        var log = loggerFactory.CreateLogger("Authagonal.Bff");

        // Correlation cookie binds this callback to the browser that started the login (login-CSRF
        // guard). The cookie is per-login, named by the state this callback claims to complete —
        // the query value is attacker-influenced, so it must be validated as cookie-name-safe
        // before being used in a lookup. Falls back to the legacy shared cookie so logins already
        // in flight across a deploy still complete.
        if (string.IsNullOrEmpty(state) || !IsCookieNameSafe(state))
            return Fail("state_mismatch");

        var perLoginCookie = CorrelationCookieFor(o, state);
        var usedLegacyCookie = false;
        // TODO(remove after 0.11.0): the legacy shared-cookie fallback only covers a login that spanned
        // the per-login-cookie deploy (pre-0.10.18). Once no such login can be in flight, drop this branch
        // and stop reading/deleting o.CorrelationCookieName.
        if (!ctx.Request.Cookies.TryGetValue(perLoginCookie, out var protectedCorr))
        {
            usedLegacyCookie = true;
            if (!ctx.Request.Cookies.TryGetValue(o.CorrelationCookieName, out protectedCorr))
                return Fail("invalid_correlation");
        }
        if (!protector.TryUnprotect(protectedCorr, CorrelationPurpose, out var corrJson))
            return Fail("invalid_correlation");

        ctx.Response.Cookies.Delete(usedLegacyCookie ? o.CorrelationCookieName : perLoginCookie, TransientCookieOptions(ctx, o));

        var correlation = JsonSerializer.Deserialize(corrJson, BffJsonContext.Default.CorrelationState);
        if (correlation is null || !FixedTimeEquals(state, correlation.State))
            return Fail("state_mismatch");

        if (!string.IsNullOrEmpty(error))
            return Fail(error);
        if (string.IsNullOrEmpty(code))
            return Fail("missing_code");

        // The correlation cookie pins which tenant this login was started for — re-resolve its client config so
        // the code exchange + id_token validation use the same confidential client and issuer.
        var tenant = await tenants.ResolveAsync(correlation.TenantKey, ct);
        if (tenant is null)
            return Fail("unknown_tenant");

        var redirectUri = BuildRedirectUri(ctx, o);

        TokenResult tokenResult;
        try
        {
            tokenResult = await tokens.ExchangeCodeAsync(tenant, code, redirectUri, correlation.CodeVerifier, ct);
        }
        catch (BffTokenException ex)
        {
            log.LogWarning(ex, "BFF code exchange failed.");
            return Fail("code_exchange_failed");
        }

        if (tokenResult.IdToken is null)
            return Fail("missing_id_token");

        var config = await oidc.GetAsync(tenant.Authority, ct);
        var handler = new JsonWebTokenHandler();
        var validation = await handler.ValidateTokenAsync(tokenResult.IdToken, new TokenValidationParameters
        {
            ValidIssuer = config.Issuer,
            ValidAudience = tenant.ClientId,
            IssuerSigningKeys = config.SigningKeys,
            ValidateLifetime = true,
        });
        if (!validation.IsValid)
        {
            log.LogWarning(validation.Exception, "BFF id_token validation failed.");
            return Fail("invalid_id_token");
        }

        var jwt = (JsonWebToken)validation.SecurityToken;
        if (!jwt.TryGetPayloadValue<string>("nonce", out var tokenNonce) || !FixedTimeEquals(tokenNonce, correlation.Nonce))
            return Fail("nonce_mismatch");

        var session = new BffSession
        {
            SessionId = Base64Url(RandomNumberGenerator.GetBytes(32)),
            TenantKey = tenant.TenantKey,
            Sid = jwt.TryGetPayloadValue<string>("sid", out var sid) ? sid : null,
            Subject = jwt.Subject,
            IdToken = tokenResult.IdToken,
            AccessToken = tokenResult.AccessToken,
            RefreshToken = tokenResult.RefreshToken,
            AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(tokenResult.ExpiresIn),
            ExpiresAt = DateTimeOffset.UtcNow.Add(o.SessionLifetime),
            Claims = ExtractClaims(jwt),
        };
        await store.SetAsync(session, ct);
        ctx.Response.Cookies.Append(o.CookieName, session.SessionId, SessionCookieOptions(ctx, o));

        return Results.Redirect(correlation.ReturnUrl);
    }

    public static async Task<IResult> UserAsync(
        HttpContext ctx,
        IOptions<AuthagonalBffOptions> options,
        IBffSessionStore store,
        BffRefreshCoordinator refresher,
        CancellationToken ct)
    {
        var o = options.Value;
        if (!HasAntiForgeryHeader(ctx, o))
            return Results.StatusCode(StatusCodes.Status401Unauthorized);

        if (!ctx.Request.Cookies.TryGetValue(o.CookieName, out var sessionId) || string.IsNullOrEmpty(sessionId))
            return Anonymous();

        var session = await store.GetAsync(sessionId, ct);
        if (session is null || session.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            ctx.Response.Cookies.Delete(o.CookieName, SessionCookieOptions(ctx, o));
            return Anonymous();
        }

        var fresh = await refresher.EnsureFreshAsync(session, ct);
        if (fresh is null)
        {
            ctx.Response.Cookies.Delete(o.CookieName, SessionCookieOptions(ctx, o));
            return Anonymous();
        }

        return Results.Json(new UserResponse
        {
            IsAuthenticated = true,
            SessionExpiresAt = fresh.ExpiresAt,
            Claims = fresh.Claims,
        }, BffJsonContext.Default.UserResponse);

        static IResult Anonymous() => Results.Json(new UserResponse { IsAuthenticated = false }, BffJsonContext.Default.UserResponse);
    }

    /// <summary>GET <c>{BasePath}/ws-ticket</c> (opt-in via <see cref="AuthagonalBffOptions.WsTicketsEnabled"/>).
    /// Mints a short-lived, single-use ticket bound to the session's (refreshed) access token, stored in the
    /// shared distributed cache under <c>agbff:wst:{ticket}</c>. The API host resolves the key, deletes it,
    /// and authenticates the websocket with the recovered token — the browser never holds the token and must
    /// never persist the ticket (fetch it, connect with it, drop it).</summary>
    public static async Task<IResult> WsTicketAsync(
        HttpContext ctx,
        IOptions<AuthagonalBffOptions> options,
        IBffSessionStore store,
        BffRefreshCoordinator refresher,
        IDistributedCache cache,
        BffExchangedTokens exchangedTokens,
        CancellationToken ct)
    {
        var o = options.Value;
        if (!HasAntiForgeryHeader(ctx, o))
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        if (!ctx.Request.Cookies.TryGetValue(o.CookieName, out var sessionId) || string.IsNullOrEmpty(sessionId))
            return Results.StatusCode(StatusCodes.Status401Unauthorized);

        var session = await store.GetAsync(sessionId, ct);
        if (session is null || session.ExpiresAt <= DateTimeOffset.UtcNow)
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        var fresh = await refresher.EnsureFreshAsync(session, ct);
        if (fresh is null)
            return Results.StatusCode(StatusCodes.Status401Unauthorized);

        // Context binding: allowlisted query params ride an RFC 8693 exchange, and the ticket is
        // bound to the resulting downscoped token instead of the primary access token. A denied
        // exchange (the tenant's transformer rejected the binding — e.g. no access to that
        // project) fails the mint: 403, no ticket.
        var ticketToken = fresh.AccessToken;
        var exchangeParams = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var param in o.TicketExchangeParams)
            if (ctx.Request.Query.TryGetValue(param, out var values) && values.FirstOrDefault() is { Length: > 0 } value)
                exchangeParams[param] = value;
        if (exchangeParams.Count > 0)
        {
            var exchanged = await exchangedTokens.GetOrExchangeAsync(fresh, fresh.AccessToken, exchangeParams, ct);
            if (exchanged is null)
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            ticketToken = exchanged;
        }

        // 256-bit random, hex — URL-safe with no padding/casing pitfalls.
        var ticket = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        await cache.SetStringAsync(WsTicketKey(ticket), ticketToken,
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = o.WsTicketLifetime }, ct);

        ctx.Response.Headers.CacheControl = "no-store";
        return Results.Json(new WsTicketResponse
        {
            Ticket = ticket,
            ExpiresInSeconds = (int)o.WsTicketLifetime.TotalSeconds,
        }, BffJsonContext.Default.WsTicketResponse);
    }

    /// <summary>Cache key a websocket ticket is stored under — the contract the resolving API host reads.
    /// Public so a host in a separate assembly can build it without hardcoding the <c>agbff:wst:</c> prefix.</summary>
    public static string WsTicketKey(string ticket) => $"agbff:wst:{ticket}";

    /// <summary>
    /// Redeem a websocket ticket minted by <c>{BasePath}/ws-ticket</c>: looks up the access token bound to
    /// it, DELETES the key so it can't be reused, and returns the token (null if the ticket is unknown,
    /// expired, or already redeemed). Call this from the API host that terminates the websocket, then
    /// authenticate the socket with the returned token — the browser only ever carries the opaque ticket.
    /// </summary>
    /// <remarks>
    /// <see cref="IDistributedCache"/> has no atomic get-and-delete, so this does Get-then-Remove: two
    /// requests reading the same ticket in the tiny window before either removes it could both succeed — a
    /// residual replay window bounded by that gap and the (30s default) TTL. For strict single-use, back
    /// the cache with a store that offers an atomic pop (e.g. Redis <c>GETDEL</c>).
    /// </remarks>
    public static async Task<string?> TryRedeemWsTicketAsync(IDistributedCache cache, string ticket, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(ticket))
            return null;
        var key = WsTicketKey(ticket);
        var token = await cache.GetStringAsync(key, ct);
        if (token is null)
            return null;
        await cache.RemoveAsync(key, ct);
        return token;
    }

    public static async Task<IResult> LogoutAsync(
        HttpContext ctx,
        string? returnUrl,
        IOptions<AuthagonalBffOptions> options,
        IBffSessionStore store,
        ITokenClient tokens,
        IBffTenantResolver tenants,
        BffOidcConfig oidc,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var o = options.Value;
        // POST is a scripted call and must present the anti-forgery header; GET is a top-level navigation
        // (following a link), which can't carry a custom header.
        if (HttpMethods.IsPost(ctx.Request.Method) && !HasAntiForgeryHeader(ctx, o))
            return Results.StatusCode(StatusCodes.Status401Unauthorized);

        // Optional caller-supplied post-logout landing (same allowlist as /login's returnUrl). Carried
        // through the RP-initiated end_session round trip via `state` so it survives the auth host clearing
        // its SSO cookie — the auth host echoes `state` back onto our registered /logout-callback, which
        // re-validates and redirects. Null when not requested → the fixed PostLogoutRedirectUri as before.
        var safeReturn = string.IsNullOrEmpty(returnUrl) ? null : SanitizeReturnUrl(returnUrl, o);

        string? idTokenHint = null;
        BffTenantConfig? tenant = null;
        if (ctx.Request.Cookies.TryGetValue(o.CookieName, out var sessionId) && !string.IsNullOrEmpty(sessionId))
        {
            var session = await store.GetAsync(sessionId, ct);
            if (session is not null)
            {
                idTokenHint = session.IdToken;
                // Re-resolve the session's tenant so revoke + end_session hit the right auth host / client.
                tenant = await tenants.ResolveAsync(session.TenantKey, ct);
                if (tenant is not null && session.RefreshToken is not null)
                {
                    try { await tokens.RevokeAsync(tenant, session.RefreshToken, ct); }
                    catch (Exception ex) { loggerFactory.CreateLogger("Authagonal.Bff").LogWarning(ex, "BFF refresh-token revocation failed on logout."); }
                }
                await store.RemoveAsync(sessionId, ct);
            }
            ctx.Response.Cookies.Delete(o.CookieName, SessionCookieOptions(ctx, o));
        }

        // The RP-initiated end_session redirect needs a tenant (its auth host + client). Without a session — or
        // an unresolvable tenant — there's nothing to sign out at the IdP; just return to the post-logout URL.
        if (tenant is not null)
        {
            var config = await oidc.GetAsync(tenant.Authority, ct);
            if (!string.IsNullOrEmpty(config.EndSessionEndpoint))
            {
                var query = new Dictionary<string, string?> { ["client_id"] = tenant.ClientId };
                if (idTokenHint is not null)
                    query["id_token_hint"] = idTokenHint;
                if (safeReturn is not null)
                {
                    // Land back on our own registered callback (exact-match validated by the auth host) and
                    // carry the real destination in `state`; the callback redirects there after SSO is cleared.
                    query["post_logout_redirect_uri"] = BuildLogoutCallbackUri(ctx, o);
                    query["state"] = safeReturn;
                }
                else if (o.PostLogoutRedirectUri is not null)
                {
                    query["post_logout_redirect_uri"] = o.PostLogoutRedirectUri;
                }
                return Results.Redirect(QueryHelpers.AddQueryString(config.EndSessionEndpoint, query));
            }
        }

        // No end_session round trip (no session / no tenant / no endpoint). The BFF cookie is already gone and
        // there is no upstream SSO to clear, so honour the requested return directly.
        if (safeReturn is not null)
            return Results.Redirect(safeReturn);
        return o.PostLogoutRedirectUri is not null ? Results.Redirect(o.PostLogoutRedirectUri) : Results.Ok();
    }

    /// <summary>
    /// GET <c>{BasePath}/logout-callback</c>. The auth host's end_session redirects here after clearing its
    /// SSO cookie, echoing the desired landing back in <c>state</c> (see <see cref="LogoutAsync"/>). We
    /// re-validate against the same allowlist and redirect — this must be registered as a
    /// <c>post_logout_redirect_uri</c> for the BFF's OIDC client.
    /// </summary>
    public static IResult LogoutCallback(HttpContext ctx, string? state, IOptions<AuthagonalBffOptions> options)
    {
        var o = options.Value;
        var safeReturn = SanitizeReturnUrl(state, o);
        return Results.Redirect(safeReturn);
    }

    // OIDC Back-Channel Logout 1.0 consumer. The IdP POSTs a signed logout_token (form-encoded,
    // server-to-server — no cookie, no browser); we validate it and kill the matching session(s).
    public static async Task<IResult> BackChannelLogoutAsync(
        HttpContext ctx,
        IOptions<AuthagonalBffOptions> options,
        BffOidcConfig oidc,
        IBffTenantResolver tenants,
        IBffSessionStore store,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var log = loggerFactory.CreateLogger("Authagonal.Bff");
        ctx.Response.Headers.CacheControl = "no-store";

        var form = await ctx.Request.ReadFormAsync(ct);
        var logoutToken = form["logout_token"].ToString();
        if (string.IsNullOrEmpty(logoutToken))
            return Fail("missing_logout_token");

        // A back-channel logout carries no session cookie — the token's issuer is all we have to pick which
        // tenant it's for. Read the (still-unvalidated) iss only to *select* the tenant; the signature is then
        // verified below against that tenant's JWKS + client id, so a forged iss can't get a token accepted.
        string issuer;
        try { issuer = new JsonWebToken(logoutToken).Issuer; }
        catch (Exception ex) { log.LogWarning(ex, "BFF back-channel logout token was not a well-formed JWT."); return Fail("invalid_logout_token"); }

        var tenant = await tenants.ResolveByIssuerAsync(issuer, ct);
        if (tenant is null)
            return Fail("unknown_issuer");

        var config = await oidc.GetAsync(tenant.Authority, ct);
        var validation = await new JsonWebTokenHandler().ValidateTokenAsync(logoutToken, new TokenValidationParameters
        {
            ValidIssuer = config.Issuer,
            ValidAudience = tenant.ClientId,
            IssuerSigningKeys = config.SigningKeys,
            ValidateLifetime = false,      // a logout token carries no exp
            RequireExpirationTime = false,
            // Pin the algorithms rather than verifying whatever the header names. Keys come from the
            // IdP's published JWKS so one cannot be injected, but this keeps that a property of our
            // code rather than of a library default.
            ValidAlgorithms =
            [
                SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSha384, SecurityAlgorithms.RsaSha512,
                SecurityAlgorithms.RsaSsaPssSha256, SecurityAlgorithms.RsaSsaPssSha384, SecurityAlgorithms.RsaSsaPssSha512,
                SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.EcdsaSha384, SecurityAlgorithms.EcdsaSha512,
            ],
        });
        if (!validation.IsValid)
        {
            log.LogWarning(validation.Exception, "BFF back-channel logout token validation failed.");
            return Fail("invalid_logout_token");
        }

        var jwt = (JsonWebToken)validation.SecurityToken;
        // A logout token MUST NOT carry a nonce and MUST carry the backchannel-logout event.
        if (jwt.TryGetPayloadValue<string>("nonce", out _))
            return Fail("nonce_not_allowed");
        if (!HasBackChannelLogoutEvent(jwt))
            return Fail("missing_logout_event");

        var hasSid = jwt.TryGetPayloadValue<string>("sid", out var sid);
        var hasSub = jwt.TryGetPayloadValue<string>("sub", out var sub);
        if (!hasSid && !hasSub)
            return Fail("missing_sub_or_sid");

        // A logout token has no exp, so without a freshness bound one stays valid forever. Capture a
        // legitimate token today, replay it after the user signs back in tomorrow, and they are logged
        // out again — repeatable, and indistinguishable from a flaky session. OIDC Back-Channel Logout
        // 1.0 §2.4 requires iat; this holds the replay window to minutes.
        if (!jwt.TryGetPayloadValue<long>("iat", out var issuedAtUnix))
            return Fail("missing_iat");
        var issuedAt = DateTimeOffset.FromUnixTimeSeconds(issuedAtUnix);
        var age = DateTimeOffset.UtcNow - issuedAt;
        if (age > LogoutTokenMaxAge || age < -LogoutTokenClockSkew)
        {
            log.LogWarning("BFF back-channel logout token iat is outside the accepted window ({Age}).", age);
            return Fail("stale_logout_token");
        }

        // Prefer sid (a single session) if the IdP scoped it that way; otherwise kill every session for
        // the subject (the form Authagonal emits).
        var removed = hasSid ? await store.RemoveBySidAsync(sid!, ct) : await store.RemoveBySubjectAsync(sub!, ct);
        log.LogInformation("BFF back-channel logout removed {Count} session(s) by {Kind}.", removed, hasSid ? "sid" : "sub");
        return Results.Ok();
    }

    // ---- helpers ----

    private const string BackChannelLogoutEventName = "http://schemas.openid.net/event/backchannel-logout";

    /// <summary>How stale a logout token may be. Generous enough for a slow IdP retry, short enough that
    /// a captured token cannot be held and replayed against a later session.</summary>
    private static readonly TimeSpan LogoutTokenMaxAge = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan LogoutTokenClockSkew = TimeSpan.FromMinutes(2);

    private static bool HasBackChannelLogoutEvent(JsonWebToken jwt)
        => jwt.TryGetPayloadValue<JsonElement>("events", out var events)
           && events.ValueKind == JsonValueKind.Object
           && events.TryGetProperty(BackChannelLogoutEventName, out _);

    private static IResult Fail(string code) => Results.Text(code, "text/plain", null, StatusCodes.Status400BadRequest);

    private static bool HasAntiForgeryHeader(HttpContext ctx, AuthagonalBffOptions o)
        => ctx.Request.Headers.ContainsKey(o.AntiForgeryHeader);

    private static string BuildRedirectUri(HttpContext ctx, AuthagonalBffOptions o)
        => $"{ctx.Request.Scheme}://{ctx.Request.Host}{o.CallbackPath}";

    private static string BuildLogoutCallbackUri(HttpContext ctx, AuthagonalBffOptions o)
        => $"{ctx.Request.Scheme}://{ctx.Request.Host}{o.BasePath}/logout-callback";

    internal static string SanitizeReturnUrl(string? returnUrl, AuthagonalBffOptions o)
    {
        if (string.IsNullOrEmpty(returnUrl))
            return "/";
        // Local relative path. Mirror ASP.NET Url.IsLocalUrl: must start '/', and the second
        // char must be neither '/' (protocol-relative "//evil.com") nor '\' — browsers normalize
        // '\'→'/' in a Location header, so "/\evil.com" would redirect off-site as "//evil.com".
        if (returnUrl.StartsWith('/') && (returnUrl.Length == 1 || (returnUrl[1] != '/' && returnUrl[1] != '\\')))
            return returnUrl;
        if (Uri.TryCreate(returnUrl, UriKind.Absolute, out var abs))
        {
            var origin = $"{abs.Scheme}://{abs.Authority}";
            if (o.ReturnUrlAllowlist.Contains(origin, StringComparer.OrdinalIgnoreCase))
                return returnUrl;
        }
        return "/";
    }

    private static Dictionary<string, string> ExtractClaims(JsonWebToken jwt)
    {
        var claims = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var claim in jwt.Claims)
        {
            if (ProtocolClaims.Contains(claim.Type)) continue;
            // Array claims (roles, groups) arrive as repeated claim types — space-join so the SPA
            // sees the full set (previously only the first value survived). NOTE: this assumes individual
            // values contain no spaces (true for roles/groups); a value with an embedded space would be
            // indistinguishable from two separate values downstream.
            claims[claim.Type] = claims.TryGetValue(claim.Type, out var existing)
                ? $"{existing} {claim.Value}"
                : claim.Value;
        }
        return claims;
    }

    private static string Base64Url(byte[] bytes) => WebEncoders.Base64UrlEncode(bytes);

    private static bool FixedTimeEquals(string a, string b)
        => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private static CookieOptions SessionCookieOptions(HttpContext ctx, AuthagonalBffOptions o) => new()
    {
        HttpOnly = true,
        Secure = IsSecure(ctx, o.CookieName),
        SameSite = SameSiteMode.Lax,
        Path = "/",
        IsEssential = true,
    };

    private static CookieOptions TransientCookieOptions(HttpContext ctx, AuthagonalBffOptions o) => new()
    {
        HttpOnly = true,
        Secure = IsSecure(ctx, o.CorrelationCookieName),
        SameSite = SameSiteMode.Lax,
        Path = "/",
        IsEssential = true,
        Expires = DateTimeOffset.UtcNow.AddMinutes(15),
    };

    private static bool IsSecure(HttpContext ctx, string cookieName)
        => ctx.Request.IsHttps
           || cookieName.StartsWith("__Host-", StringComparison.Ordinal)
           || cookieName.StartsWith("__Secure-", StringComparison.Ordinal);

    private static string CorrelationCookieFor(AuthagonalBffOptions o, string state)
        => $"{o.CorrelationCookieName}.{state}";

    // Our states are base64url(32 bytes) = 43 chars; anything else in the callback query is not a
    // state we issued and must not end up inside a cookie name (header-injection surface).
    private static bool IsCookieNameSafe(string state)
        => state.Length <= 64 && state.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
