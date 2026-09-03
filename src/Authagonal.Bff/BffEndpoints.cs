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
        // Evict the oldest outstanding correlation cookies before adding another.
        //
        // /bff/login is an unauthenticated GET and each call appends a cookie with a FRESH name (the per-login
        // state), while the callback deletes only the one whose state it presented. Nothing pruned the rest, so
        // they accumulated for their full 15-minute expiry at roughly 400 bytes each. Kestrel's
        // MaxRequestHeadersTotalSize defaults to 32 KB, so on the order of 80 outstanding cookies make every
        // subsequent request to the SPA origin fail with 431 — a cookie-bomb denial of service against the
        // origin, self-inflicted by repeatedly hitting a public endpoint, and not clearable by the user without
        // manually deleting cookies.
        //
        // Concurrent logins are the reason these are per-login in the first place (another tab's 401 restart, a
        // user pausing on the IdP's interstitial), so the cap has to leave room for several. Evicting oldest-first
        // keeps the newest flows — the ones most likely to still complete.
        PruneCorrelationCookies(ctx, o);

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
                return RestartLoginOrFail(ctx, o, "invalid_correlation", log, "no correlation cookie for this state");
        }
        if (!protector.TryUnprotect(protectedCorr, CorrelationPurpose, out var corrJson))
            return RestartLoginOrFail(ctx, o, "invalid_correlation", log, "correlation cookie could not be unprotected");

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
            // The 0.20.0 sweep pinned every inbound-token path — SAML, client assertions, upstream
            // id_tokens, the logout consumer below, token exchange — and missed this one, which is
            // the same class of token. OIDC Core §3.1.3.7 step 7 expects the client to know the
            // algorithm it registered for rather than take it from the token header.
            ValidAlgorithms = BffClaims.AsymmetricSigningAlgorithms,
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
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
            Claims = BffClaims.Extract(jwt),
        };
        await store.SetAsync(session, ct);
        // This login completed, so the one-shot restart marker has done its job; leaving it would
        // deny the NEXT interrupted login its retry.
        ctx.Response.Cookies.Delete(o.CookieName + RetrySuffix, TransientCookieOptions(ctx, o));
        // Persistent cookie (opt-in): survives browser close so a session whose refresh token is
        // still valid isn't thrown away on the next launch. Bounded to the session's own expiry, so
        // cookie and server session lapse together. Default stays a session cookie. Never set MaxAge
        // on the delete path (SessionCookieOptions) — MaxAge would win over the epoch Expires and the
        // cookie would refuse to clear.
        var sessionCookie = SessionCookieOptions(ctx, o);
        if (o.PersistentCookie)
            sessionCookie.MaxAge = session.ExpiresAt - DateTimeOffset.UtcNow;
        ctx.Response.Cookies.Append(o.CookieName, session.SessionId, sessionCookie);

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

        // no-store, as /bff/ws-ticket already sets. Every response here reports authentication state, and
        // the authenticated one carries the user's identity claims — keyed by nothing but the session
        // cookie. A shared cache, or a browser applying heuristic freshness to a 200 with no validators,
        // could otherwise serve one user's claims to the next request on the same connection, and the SPA
        // polls this endpoint. Set before the branches so the anonymous answers are uncacheable too: a
        // stale "isAuthenticated: false" keeps a signed-in user looking signed out.
        ctx.Response.Headers.CacheControl = "no-store";
        ctx.Response.Headers.Vary = "Cookie";

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
            ValidAlgorithms = BffClaims.AsymmetricSigningAlgorithms,
            // Both default to something weaker than the callback path sets: IdentityModel 8.17.0
            // defaults ValidateIssuerSigningKey to false, so the callback checked signing-key validity
            // periods and this consumer did not. Set explicitly rather than inherited, so the two
            // consumers of the same JWKS agree.
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
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
        // Scoped to the tenant whose IdP signed this token — already resolved above, and previously
        // discarded. `sub` is unique only within an issuer, so an unscoped removal let a logout
        // accepted from one tenant terminate another tenant's sessions for a colliding subject; the
        // endpoint accepts a valid token from ANY configured tenant, which makes that a cross-tenant
        // denial of service.
        var removed = hasSid
            ? await store.RemoveBySidAsync(sid!, tenant.TenantKey, ct)
            : await store.RemoveBySubjectAsync(sub!, tenant.TenantKey, ct);
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

    /// <summary>Marks that this browser has already been sent back through the login leg once.</summary>
    private const string RetrySuffix = ".retry";

    /// <summary>
    /// A callback with no usable correlation cookie is usually not an attack and not a dead end: the
    /// login simply took longer than the cookie lived, most often because it spanned an email
    /// verification. The user is authenticated at the provider by now, so starting a fresh login leg
    /// completes silently through SSO and lands them signed in — where returning
    /// <c>invalid_correlation</c> as plain text shows them a broken page for something they did
    /// nothing wrong to cause.
    /// </summary>
    /// <remarks>
    /// Restarting mints a new state, nonce and PKCE verifier, so it is not a weakening — the old
    /// correlation is discarded, not trusted. Exactly one retry, tracked by a short-lived cookie, so
    /// a browser that cannot keep cookies at all fails visibly instead of bouncing forever.
    /// </remarks>
    private static IResult RestartLoginOrFail(
        HttpContext ctx, AuthagonalBffOptions o, string code, ILogger log, string reason)
    {
        var retryCookie = o.CookieName + RetrySuffix;
        // What distinguishes the causes is how many correlation cookies the browser still holds:
        // none at all points at a browser that never ran the login leg (a different browser, or
        // cookies cleared); several points at eviction by PruneCorrelationCookies after repeated
        // login attempts; exactly the expected one missing while others remain points at a callback
        // replayed after its cookie was consumed. Guessing between them cost a round of theories.
        var outstanding = ctx.Request.Cookies.Keys.Count(k => k.StartsWith(o.CorrelationCookieName, StringComparison.Ordinal));
        if (ctx.Request.Cookies.ContainsKey(retryCookie))
        {
            log.LogWarning(
                "BFF callback failed again after a restart ({Reason}); {Outstanding} correlation cookie(s) present. "
                + "Giving up rather than looping — this browser is not keeping the cookie.", reason, outstanding);
            ctx.Response.Cookies.Delete(retryCookie, TransientCookieOptions(ctx, o));
            return Fail(code);
        }
        log.LogInformation(
            "BFF callback has no usable correlation ({Reason}); {Outstanding} correlation cookie(s) present. "
            + "Restarting the login once — the provider session should complete it silently.", reason, outstanding);
        ctx.Response.Cookies.Append(retryCookie, "1", TransientCookieOptions(ctx, o));
        return Results.Redirect($"{o.BasePath}/login");
    }

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
        // Shared local-path decision. This used to inspect only returnUrl[1], so a backslash later in the
        // path slipped through, and (like every other copy) it did not reject the ASCII tab that the URL
        // parser strips before parsing.
        if (Authagonal.Core.Services.LocalRedirect.IsSafeLocalPath(returnUrl))
            return returnUrl;
        if (Uri.TryCreate(returnUrl, UriKind.Absolute, out var abs))
        {
            var origin = $"{abs.Scheme}://{abs.Authority}";
            if (o.ReturnUrlAllowlist.Contains(origin, StringComparer.OrdinalIgnoreCase))
                return returnUrl;
        }
        return "/";
    }


    private static string Base64Url(byte[] bytes) => WebEncoders.Base64UrlEncode(bytes);

    /// <summary>
    /// The signature algorithms this BFF will accept on any token it consumes from the IdP.
    /// </summary>
    /// <remarks>
    /// Shared by the callback id_token and the back-channel logout token so the two cannot drift —
    /// they already had, which is what F266/F277 recorded. Keys come from the IdP's published JWKS so
    /// one cannot be injected, but pinning keeps the accepted set a property of this code rather than
    /// of a library default, and excludes the symmetric algorithms that make key confusion possible.
    /// </remarks>

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
        Expires = DateTimeOffset.UtcNow.Add(o.CorrelationLifetime),
    };

    private static bool IsSecure(HttpContext ctx, string cookieName)
        => ctx.Request.IsHttps
           || cookieName.StartsWith("__Host-", StringComparison.Ordinal)
           || cookieName.StartsWith("__Secure-", StringComparison.Ordinal);

    private static string CorrelationCookieFor(AuthagonalBffOptions o, string state)
        => $"{o.CorrelationCookieName}.{state}";

    /// <summary>
    /// How many in-flight logins one browser may have against this origin at once.
    /// </summary>
    /// <remarks>
    /// Generous for the legitimate case — concurrent logins are why these cookies are per-login — and far below
    /// the point where the accumulated header breaks the origin.
    /// </remarks>
    internal const int MaxOutstandingCorrelationCookies = 8;

    /// <summary>
    /// Deletes the oldest outstanding correlation cookies so at most
    /// <see cref="MaxOutstandingCorrelationCookies"/> - 1 survive alongside the one about to be written.
    /// </summary>
    /// <remarks>
    /// The request's cookie order is the browser's, which is not a reliable age ordering — so this deletes by
    /// position and accepts that "oldest" is approximate. What matters is the bound: the number of these cookies
    /// cannot grow without limit, whatever order they are pruned in. Deleting a correlation cookie only costs
    /// whoever holds that flow a restart of their login, which is already the outcome when one expires.
    /// </remarks>
    private static void PruneCorrelationCookies(HttpContext ctx, AuthagonalBffOptions o)
    {
        var prefix = o.CorrelationCookieName + ".";
        var outstanding = ctx.Request.Cookies.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
            .ToList();

        var excess = outstanding.Count - (MaxOutstandingCorrelationCookies - 1);
        if (excess <= 0) return;

        foreach (var stale in outstanding.Take(excess))
            ctx.Response.Cookies.Delete(stale, TransientCookieOptions(ctx, o));
    }

    // Our states are base64url(32 bytes) = 43 chars; anything else in the callback query is not a
    // state we issued and must not end up inside a cookie name (header-injection surface).
    private static bool IsCookieNameSafe(string state)
        => state.Length <= 64 && state.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
