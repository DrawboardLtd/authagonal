using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.Server.Services;
using Authagonal.Server.Services.Oidc;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Authagonal.Server.Endpoints;

public static class OidcEndpoints
{
    /// <summary>
    /// Signing algorithms accepted on an upstream IdP's id_token. Asymmetric only — an HMAC algorithm
    /// here is the key-confusion shape, where the IdP's public key doubles as a shared secret the
    /// attacker also has, and the absence of any entry would permit `alg: none`.
    /// </summary>
    private static readonly string[] UpstreamIdTokenAlgorithms =
    [
        "RS256", "RS384", "RS512",
        "PS256", "PS384", "PS512",
        "ES256", "ES384", "ES512",
    ];

    // Browser-binding cookie for the federation state (F48d). Scoped to /oidc so it rides the login→callback
    // navigation only.
    private const string StateCookieName = "oidc_state";

    public static IEndpointRouteBuilder MapOidcEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/oidc/{connectionId}/login", LoginAsync).AllowAnonymous();
        app.MapGet("/oidc/callback", CallbackAsync).AllowAnonymous();

        return app;
    }

    private static async Task<IResult> LoginAsync(
        HttpContext httpContext,
        string connectionId,
        string? returnUrl,
        string? loginHint,
        IOidcProviderStore oidcStore,
        OidcDiscoveryClient discoveryClient,
        Authagonal.Core.Services.IOidcStateStore stateStore,
        Authagonal.Core.Services.ITenantContext tenantContext,
        ILogger<Program> logger,
        CancellationToken ct)
    {
        var config = await oidcStore.GetAsync(connectionId, ct);
        if (config is null)
            return Results.NotFound(new { error = "not_found", error_description = $"OIDC connection '{connectionId}' not found" });

        // Fetch discovery document
        var discovery = await discoveryClient.GetDiscoveryAsync(config.MetadataLocation, ct);

        // Generate PKCE parameters
        var stateBytes = RandomNumberGenerator.GetBytes(32);
        var state = Base64UrlEncode(stateBytes);

        var nonceBytes = RandomNumberGenerator.GetBytes(32);
        var nonce = Base64UrlEncode(nonceBytes);

        var codeVerifierBytes = RandomNumberGenerator.GetBytes(32);
        var codeVerifier = Base64UrlEncode(codeVerifierBytes);

        var codeChallenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));

        // Store state (validate returnUrl to prevent open redirect)
        var effectiveReturnUrl = SanitizeReturnUrl(returnUrl);
        await stateStore.StoreAsync(state, connectionId, effectiveReturnUrl, codeVerifier, nonce, ct);

        // F48d: bind this attempt to the initiating browser. The callback requires a cookie matching the
        // state param, so an attacker can't run the federation for their own identity and deliver the
        // callback URL to a victim (login CSRF — the nonce binds the id_token to the state, not the
        // browser). SameSite=Lax survives the top-level GET redirect back from the IdP.
        httpContext.Response.Cookies.Append(StateCookieName, state, new CookieOptions
        {
            HttpOnly = true,
            Secure = httpContext.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = "/oidc",
            Expires = DateTimeOffset.UtcNow.AddMinutes(15),
        });

        // Build authorization URL
        var baseUrl = tenantContext.Issuer;
        var redirectUri = $"{baseUrl}/oidc/callback";

        // Forward the originally-requested scopes from the downstream RP so the upstream
        // releases the same claim set. Scope rides in the /authorize URL preserved as
        // returnUrl when the host's auth middleware challenges the cookie scheme. If
        // returnUrl isn't an /authorize URL or has no scope, fall back to the OIDC baseline.
        var upstreamScope = ExtractScopeFromReturnUrl(effectiveReturnUrl)
            ?? "openid profile email";

        // Upstream-federated refresh: force offline_access on the hop so the upstream issues a refresh
        // token we can redeem to revalidate the session on each local refresh (Option A). Idempotent.
        if (config.RevalidateOnRefresh &&
            !upstreamScope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("offline_access"))
        {
            upstreamScope += " offline_access";
        }

        // '?' only when the endpoint has no query of its own. Several IdPs publish an
        // authorization_endpoint that already carries one (a tenant or policy parameter — Azure AD B2C
        // and PingFederate both do), and appending a second '?' produced a URL whose first real
        // parameter was swallowed into the previous value. The whole federation then failed with an
        // error from the upstream that named a parameter this server had sent correctly.
        var authorizeSeparator = discovery.AuthorizationEndpoint.Contains('?') ? '&' : '?';

        var authorizationUrl = $"{discovery.AuthorizationEndpoint}" +
            $"{authorizeSeparator}client_id={Uri.EscapeDataString(config.ClientId)}" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            $"&response_type=code" +
            $"&scope={Uri.EscapeDataString(upstreamScope)}" +
            $"&state={Uri.EscapeDataString(state)}" +
            $"&nonce={Uri.EscapeDataString(nonce)}" +
            $"&code_challenge={Uri.EscapeDataString(codeChallenge)}" +
            $"&code_challenge_method=S256";

        // Straight-to-IdP prefill: the authorize endpoint appends &loginHint= for an SSO-domain email.
        // Forward it as the standard OIDC login_hint so the upstream can prefill its username field.
        if (!string.IsNullOrWhiteSpace(loginHint))
            authorizationUrl += $"&login_hint={Uri.EscapeDataString(loginHint)}";

        // Whitelisted passthroughs from the downstream /authorize request to upstream.
        // Source order: returnUrl query first (canonical, since returnUrl IS the
        // original /authorize URL), then this endpoint's own query as a fallback so
        // ad-hoc callers can pass values directly. The whitelist is the OidcProviderConfig
        // contract — anything not on it is dropped.
        if (config.PassthroughParams.Count > 0)
        {
            var passthroughs = CollectPassthroughs(
                config.PassthroughParams, effectiveReturnUrl, httpContext.Request.Query);
            foreach (var (key, value) in passthroughs)
            {
                authorizationUrl += $"&{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}";
            }
        }

        logger.LogInformation("OIDC login initiated for connection {ConnectionId}, returnUrl={ReturnUrl}", connectionId, effectiveReturnUrl);

        return Results.Redirect(authorizationUrl);
    }

    private static async Task<IResult> CallbackAsync(
        HttpContext httpContext,
        IOidcProviderStore oidcStore,
        IUserStore userStore,
        IClientStore clientStore,
        IGrantStore grantStore,
        IMfaStore mfaStore,
        WebAuthnService webAuthnService,
        IEnumerable<IAuthHook> authHooks,
        OidcDiscoveryClient discoveryClient,
        Authagonal.Core.Services.IOidcStateStore stateStore,
        IHttpClientFactory httpClientFactory,
        ISecretProvider secretProvider,
        Authagonal.Core.Services.ITenantContext tenantContext,
        IProvisioningOrchestrator provisioning,
        ISsoDomainStore ssoDomainStore,
        IConfiguration configuration,
        IOptions<AuthOptions> authOptions,
        ILogger<Program> logger,
        CancellationToken ct)
    {
        var query = httpContext.Request.Query;
        var code = query["code"].ToString();
        var state = query["state"].ToString();

        // Check for error from the IdP
        var idpError = query["error"].ToString();
        if (!string.IsNullOrEmpty(idpError))
        {
            var idpErrorDescription = query["error_description"].ToString();
            logger.LogWarning("OIDC IdP returned error: {Error} - {Description}", idpError, idpErrorDescription);

            // We don't have returnUrl without valid state, redirect to login with error
            return Results.Redirect($"/login?error=oidc_error&error_description={Uri.EscapeDataString(idpErrorDescription ?? idpError)}");
        }

        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
            return Results.BadRequest(new { error = "missing_parameters", error_description = "Missing code or state parameter" });

        // F48d: the state must match the browser-bound cookie set at /oidc/{id}/login (login-CSRF defense).
        // Checked before consuming state so a cross-browser callback can't burn a victim's pending state.
        var boundState = httpContext.Request.Cookies[StateCookieName];
        httpContext.Response.Cookies.Delete(StateCookieName, new CookieOptions { Path = "/oidc" });
        if (string.IsNullOrEmpty(boundState) ||
            !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(boundState), Encoding.UTF8.GetBytes(state)))
        {
            logger.LogWarning("OIDC state cookie missing or mismatched — possible login CSRF");
            return Results.BadRequest(new { error = "invalid_state", error_description = "State binding validation failed" });
        }

        // Consume state
        var stateData = await stateStore.ConsumeAsync(state, ct);
        if (stateData is null)
        {
            logger.LogWarning("OIDC state not found or expired for state parameter");
            return Results.BadRequest(new { error = "invalid_state", error_description = "State parameter is invalid or expired" });
        }

        var returnUrl = stateData.ReturnUrl;

        // Load OIDC provider config
        var config = await oidcStore.GetAsync(stateData.ConnectionId, ct);
        if (config is null)
        {
            logger.LogWarning("OIDC connection {ConnectionId} not found during callback", stateData.ConnectionId);
            return RedirectWithError(returnUrl, "oidc_error", "OIDC connection not found");
        }

        // Fetch discovery document
        OidcDiscoveryDocument discovery;
        try
        {
            discovery = await discoveryClient.GetDiscoveryAsync(config.MetadataLocation, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to fetch OIDC discovery document for connection {ConnectionId}", stateData.ConnectionId);
            return RedirectWithError(returnUrl, "oidc_error", "Failed to fetch provider configuration");
        }

        // Exchange code for tokens
        var baseUrl = tenantContext.Issuer;
        var redirectUri = $"{baseUrl}/oidc/callback";

        // Resolve the client secret (may be a Key Vault reference)
        var clientSecret = await secretProvider.ResolveAsync(config.ClientSecret, ct);

        string idToken;
        string? accessToken;
        string? upstreamRefreshToken;
        try
        {
            (idToken, accessToken, upstreamRefreshToken) = await ExchangeCodeForTokensAsync(
                httpClientFactory, discovery.TokenEndpoint, code, redirectUri,
                config.ClientId, clientSecret, stateData.CodeVerifier, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "OIDC token exchange failed for connection {ConnectionId}", stateData.ConnectionId);
            return RedirectWithError(returnUrl, "oidc_error", "Token exchange failed");
        }

        // Validate id_token
        JsonWebTokenHandler tokenHandler = new();
        TokenValidationResult validationResult;
        try
        {
            var validationParameters = new TokenValidationParameters
            {
                ValidIssuer = discovery.Issuer,
                ValidAudience = config.ClientId,
                IssuerSigningKeys = discovery.SigningKeys,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(5),
                // Pin the signing algorithms to asymmetric ones. The keys already come from the upstream's
                // discovery document rather than from the token, which is what stops key substitution;
                // this closes the other half — `alg: none`, and the HS256 confusion where a token is
                // HMAC'd using the IdP's PUBLIC key as the shared secret. Both are refused by the
                // handler's defaults today, so this states the requirement rather than repairing a hole,
                // and keeps it true if those defaults ever move.
                ValidAlgorithms = UpstreamIdTokenAlgorithms,
            };

            validationResult = await tokenHandler.ValidateTokenAsync(idToken, validationParameters);

            if (!validationResult.IsValid)
            {
                logger.LogWarning("OIDC id_token validation failed: {Error}", validationResult.Exception?.Message);
                return RedirectWithError(returnUrl, "oidc_error", "ID token validation failed");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "OIDC id_token validation threw for connection {ConnectionId}", stateData.ConnectionId);
            return RedirectWithError(returnUrl, "oidc_error", "ID token validation failed");
        }

        // OIDC Core §3.1.3.7 steps 4-5 — the multi-audience case.
        //
        // ValidAudience = config.ClientId is satisfied by IdentityModel when ANY entry of a
        // multi-valued aud matches, so an id_token minted for this client AND others passed with no
        // further questions. The spec's answer is azp: when aud has more than one value azp MUST be
        // present, and wherever azp appears it MUST be this client. Without it, a token the upstream
        // IdP issued for a different relying party — one that merely also lists us — is accepted as
        // if it were issued to us.
        // Counting the audiences is fiddlier than it looks, and getting it wrong made this whole check
        // dead code once already. TokenValidationResult.Claims boxes a REPEATED claim into a
        // List<object>, which is not IEnumerable<string>; and a single aud arrives as a string, which
        // IS IEnumerable<char> but not IEnumerable<string>. So matching on IEnumerable<string> matched
        // neither shape, audiences was always 0 or 1, and the multi-audience branch below could never
        // run. String is tested first here precisely because a string is itself enumerable.
        var audiences = validationResult.Claims.TryGetValue("aud", out var audClaim) switch
        {
            true when audClaim is string => 1,
            true when audClaim is System.Collections.IEnumerable many => many.Cast<object>().Count(),
            true when audClaim is not null => 1,
            _ => 0,
        };
        var azp = Claim(validationResult.Claims, "azp");
        if (audiences > 1 && string.IsNullOrEmpty(azp))
        {
            logger.LogWarning("OIDC id_token has {Count} audiences but no azp for connection {ConnectionId}",
                audiences, stateData.ConnectionId);
            return RedirectWithError(returnUrl, "oidc_error", "ID token validation failed");
        }
        if (!string.IsNullOrEmpty(azp) && !string.Equals(azp, config.ClientId, StringComparison.Ordinal))
        {
            logger.LogWarning("OIDC id_token azp does not match this client for connection {ConnectionId}",
                stateData.ConnectionId);
            return RedirectWithError(returnUrl, "oidc_error", "ID token validation failed");
        }

        // Verify nonce — must be present and match the stored value
        var nonceClaim = Claim(validationResult.Claims, "nonce");
        if (string.IsNullOrEmpty(nonceClaim) ||
            !string.Equals(nonceClaim, stateData.Nonce, StringComparison.Ordinal))
        {
            logger.LogWarning("OIDC nonce missing or mismatch for connection {ConnectionId}", stateData.ConnectionId);
            return RedirectWithError(returnUrl, "oidc_error", "Nonce validation failed");
        }

        // Extract claims from validated id_token
        var sub = Claim(validationResult.Claims, "sub");
        var email = ExtractEmail(validationResult.Claims);
        var emailVerified = validationResult.Claims.TryGetValue("email_verified", out var evClaim)
            && (evClaim is bool evBool ? evBool : bool.TryParse(evClaim?.ToString(), out var evParsed) && evParsed);
        var name = Claim(validationResult.Claims, "name");
        var givenName = Claim(validationResult.Claims, "given_name");
        var familyName = Claim(validationResult.Claims, "family_name");

        // If no email in id_token, try userinfo endpoint
        if (string.IsNullOrEmpty(email) && !string.IsNullOrEmpty(accessToken) && !string.IsNullOrEmpty(discovery.UserinfoEndpoint))
        {
            try
            {
                var userinfoClaims = await FetchUserinfoAsync(httpClientFactory, discovery.UserinfoEndpoint, accessToken, ct);

                // OIDC Core 5.3.2 (F48b): the userinfo `sub` MUST match the id_token `sub`, else the
                // response may describe a DIFFERENT subject — ignore it rather than adopt its email.
                var userinfoSub = userinfoClaims.GetValueOrDefault("sub") as string;
                if (string.IsNullOrEmpty(userinfoSub) || !string.Equals(userinfoSub, sub, StringComparison.Ordinal))
                {
                    logger.LogWarning("OIDC userinfo sub mismatch for connection {ConnectionId}; ignoring userinfo response", stateData.ConnectionId);
                }
                else
                {
                    var userinfoEmail = ExtractEmailFromJson(userinfoClaims);
                    if (string.IsNullOrEmpty(email) && !string.IsNullOrEmpty(userinfoEmail))
                    {
                        email = userinfoEmail;
                        emailVerified = userinfoClaims.TryGetValue("email_verified", out var uev)
                            && (uev is bool uevBool ? uevBool : bool.TryParse(uev?.ToString(), out var uevParsed) && uevParsed);
                    }
                    name ??= userinfoClaims.GetValueOrDefault("name") as string;
                    givenName ??= userinfoClaims.GetValueOrDefault("given_name") as string;
                    familyName ??= userinfoClaims.GetValueOrDefault("family_name") as string;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to fetch userinfo for connection {ConnectionId}", stateData.ConnectionId);
            }
        }

        if (string.IsNullOrEmpty(email))
        {
            logger.LogWarning("No email found in OIDC claims for connection {ConnectionId}", stateData.ConnectionId);
            return RedirectWithError(returnUrl, "oidc_error", "No email address found in identity token");
        }

        if (string.IsNullOrEmpty(sub))
        {
            logger.LogWarning("No sub claim found in OIDC id_token for connection {ConnectionId}", stateData.ConnectionId);
            return RedirectWithError(returnUrl, "oidc_error", "No subject identifier found in identity token");
        }

        email = email.ToLowerInvariant();

        // Derive first/last name from "name" if given_name/family_name are not present
        if (string.IsNullOrEmpty(givenName) && !string.IsNullOrEmpty(name))
        {
            var parts = name.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            givenName = parts.Length > 0 ? parts[0] : null;
            familyName ??= parts.Length > 1 ? parts[1] : null;
        }

        // Enforce the connection's allowed email domains (when configured): a connection may only
        // assert identities within its own domain(s). AllowedDomains is also the admin's explicit vouch
        // that this IdP owns the domain — required (F36) before attaching to a PRE-EXISTING local account.
        var emailDomain = email.Contains('@') ? email[(email.LastIndexOf('@') + 1)..] : "";
        var domainAllowed = config.AllowedDomains is { Count: > 0 } &&
            config.AllowedDomains.Any(d => string.Equals(d, emailDomain, StringComparison.OrdinalIgnoreCase));
        if (config.AllowedDomains is { Count: > 0 } && !domainAllowed)
        {
            logger.LogWarning("OIDC email domain '{Domain}' not permitted for connection {ConnectionId}", emailDomain, stateData.ConnectionId);
            return RedirectWithError(returnUrl, "access_denied", "Your email domain is not permitted for this connection.");
        }

        // Resolve a returning user by their STABLE federated identity (provider + subject) — never by
        // email alone, which an upstream could spoof.
        var provider = $"oidc:{stateData.ConnectionId}";
        var providerKey = sub;
        var existingLogin = await userStore.FindLoginAsync(provider, providerKey, ct);
        var user = existingLogin is not null ? await userStore.GetAsync(existingLogin.UserId, ct) : null;

        // Attach this IdP to an existing local account by email ONLY when the connection is explicitly
        // authorised for that email's domain (AllowedDomains vouches the IdP owns it). email_verified is
        // an upstream-controlled boolean and is NOT sufficient to seize a pre-existing (possibly admin)
        // account — this matches SAML's stance (F36) and closes the takeover against a permissive IdP.
        if (user is null)
        {
            var existingByEmail = await userStore.FindByEmailAsync(email, ct);
            if (existingByEmail is not null)
            {
                if (!domainAllowed && !config.EffectiveAutoLinkExistingByEmail)
                {
                    logger.LogWarning("OIDC login rejected: email {Email} matches an existing account but connection {ConnectionId} is not authorised for its domain", email, stateData.ConnectionId);
                    return RedirectWithError(returnUrl, "access_denied", "This email already belongs to an account. Contact your administrator to link it.");
                }

                // Same gate as the JIT branch below, for the same reason: AllowedDomains (and more so
                // AutoLinkExistingByEmail) is this connection's own claim, and adoption is what turns a
                // squatted account into a takeover. When the routing table says another connection is the
                // authority for the domain, this one does not get to attach itself to the account.
                var adoptDomain = email.Split('@').Last().ToLowerInvariant();
                var adoptOwner = await ssoDomainStore.GetAsync(adoptDomain, ct);
                if (adoptOwner is not null && !string.Equals(adoptOwner.ConnectionId, stateData.ConnectionId, StringComparison.Ordinal))
                {
                    logger.LogWarning(
                        "OIDC adoption rejected: domain {Domain} is routed to connection {Owner}, not {ConnectionId}",
                        adoptDomain, adoptOwner.ConnectionId, stateData.ConnectionId);
                    return RedirectWithError(returnUrl, "access_denied",
                        "This email domain is managed by a different identity provider. Contact your administrator.");
                }

                // The check above asks whether this connection owns the domain. It cannot ask who else can
                // already sign in to this account — which is the question the squat actually turns on, because
                // at squat time the domain has no row at all and every gate passes. See
                // FederationAdoptionPolicy.
                var adoptAuthority = domainAllowed
                    || (adoptOwner is not null
                        && string.Equals(adoptOwner.ConnectionId, stateData.ConnectionId, StringComparison.Ordinal));
                var foreignBindings = FederationAdoptionPolicy.ForeignBindings(
                    await userStore.GetLoginsAsync(existingByEmail.Id, ct), provider);

                switch (FederationAdoptionPolicy.Evaluate(adoptAuthority, foreignBindings.Count))
                {
                    case FederationAdoptionPolicy.Decision.Refuse:
                        logger.LogWarning(
                            "OIDC adoption rejected: account {Email} carries {Count} federation binding(s) from "
                            + "other connection(s) and {ConnectionId} is not the authority for domain {Domain}",
                            email, foreignBindings.Count, stateData.ConnectionId, adoptDomain);
                        return RedirectWithError(returnUrl, "access_denied",
                            "This email already belongs to an account linked to a different identity provider. "
                            + "Contact your administrator.");

                    case FederationAdoptionPolicy.Decision.EvictForeignBindingsThenAdopt:
                        foreach (var stale in foreignBindings)
                        {
                            logger.LogWarning(
                                "OIDC adoption evicting federation binding {Provider} from account {Email}: "
                                + "connection {ConnectionId} is the authority for domain {Domain}",
                                stale.Provider, email, stateData.ConnectionId, adoptDomain);
                            await userStore.RemoveLoginAsync(stale.UserId, stale.Provider, stale.ProviderKey, ct);
                        }
                        break;
                }

                user = existingByEmail;
            }
        }

        if (user is null)
        {
            // Whitelisted authorize-request params (e.g. an org invite's acceptKind/acceptToken) that ride
            // the return URL, captured to carry the downstream provisioning context onto the JIT user.
            // User-supplied — the downstream provisioner is the gate on the VALUES; only whitelisted keys
            // are kept. Side-effect-free, so it's safe to compute before the gate decision below.
            var provisioningAttributes = config.ProvisioningAttributeParams.Count > 0
                ? CollectPassthroughs(config.ProvisioningAttributeParams, stateData.ReturnUrl, httpContext.Request.Query).ToList()
                : [];

            // Shared JIT gate (see FederationJitPolicy — same decision as the SAML path).
            switch (FederationJitPolicy.Evaluate(config.JitProvisioningEnabled, config.ProvisioningAttributeParams.Count, provisioningAttributes.Count, config.AllowUninvitedJit))
            {
                case FederationJitPolicy.Decision.RejectJitDisabled:
                    logger.LogInformation("JIT provisioning disabled for OIDC connection {ConnectionId}, rejecting unknown user {Email}", stateData.ConnectionId, email);
                    return RedirectWithError(returnUrl, "access_denied", "User not found. Contact your administrator to be provisioned.");
                case FederationJitPolicy.Decision.RejectInviteRequired:
                    logger.LogInformation("JIT rejected for OIDC connection {ConnectionId}: no provisioning context on the request for unknown user {Email}", stateData.ConnectionId, email);
                    return RedirectWithError(returnUrl, "access_denied", "This login requires an invitation. Contact your administrator.");
            }

            // An upstream that will not vouch for the address must not mint an account bearing it. This stops
            // a MISCONFIGURED upstream, and nothing more: `email_verified` is a claim chosen by whoever
            // operates the OP, so an attacker who configured the connection simply sets it true.
            //
            // It was previously described here as closing nOAuth-shaped takeover by squatting. It does not,
            // and neither does the domain-routing check below — at squat time the victim domain has not
            // onboarded, so it has no row and the check passes. What closes the takeover is that adoption no
            // longer inherits another connection's login binding; see FederationAdoptionPolicy.
            if (!emailVerified)
            {
                logger.LogWarning(
                    "JIT rejected for OIDC connection {ConnectionId}: upstream did not verify email {Email}",
                    stateData.ConnectionId, email);
                return RedirectWithError(returnUrl, "access_denied",
                    "Your identity provider did not verify this email address. Contact your administrator.");
            }

            // And the domain must not already belong to a DIFFERENT connection. Otherwise a permissive
            // connection can squat addresses in a domain another connection is the authority for.
            var jitEmailDomain = email.Split('@').Last().ToLowerInvariant();
            var domainOwner = await ssoDomainStore.GetAsync(jitEmailDomain, ct);
            if (domainOwner is not null && !string.Equals(domainOwner.ConnectionId, stateData.ConnectionId, StringComparison.Ordinal))
            {
                logger.LogWarning(
                    "JIT rejected: domain {Domain} is routed to connection {Owner}, not {ConnectionId}",
                    jitEmailDomain, domainOwner.ConnectionId, stateData.ConnectionId);
                return RedirectWithError(returnUrl, "access_denied",
                    "This email domain is managed by a different identity provider. Contact your administrator.");
            }

            user = new AuthUser
            {
                // Trusted first-party connections (e.g. bullclip's guest-link provider) adopt the
                // upstream subject as the local user id so the downstream RP's own user id survives.
                Id = config.EffectiveUseUpstreamSubjectAsUserId ? providerKey : Guid.NewGuid().ToString("N"),
                Email = email,
                NormalizedEmail = email.ToUpperInvariant(),
                EmailConfirmed = emailVerified,
                FirstName = givenName,
                LastName = familyName,
                CreatedAt = DateTimeOffset.UtcNow,
                LockoutEnabled = true,
                SecurityStamp = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            };

            foreach (var kv in provisioningAttributes)
                user.CustomAttributes[kv.Key] = kv.Value;

            // Uninvited SSO auto-provision: tag the user with the connection they federated through so the
            // downstream provisioner can place them in that tenant rather than creating a new one. Only when
            // there was no invite context. Mirrors the SAML path.
            if (config.AllowUninvitedJit && provisioningAttributes.Count == 0)
                user.CustomAttributes["federated_connection"] = config.ConnectionName;

            await userStore.CreateAsync(user, ct);

            try
            {
                await provisioning.ProvisionAsync(user, ct);
            }
            catch (ProvisioningException ex)
            {
                await userStore.DeleteAsync(user.Id, ct);
                logger.LogWarning(ex, "Provisioning rejected OIDC SSO user {Email}", email);
                return Results.BadRequest(new { error = "provisioning_rejected", message = ex.Message });
            }

            logger.LogInformation("Created new user {UserId} ({Email}) via OIDC SSO", user.Id, email);
            await authHooks.RunOnUserCreatedAsync(user.Id, email, "oidc", ct);
        }
        else
        {
            // Update name fields if they were empty and we now have them
            var updated = false;
            if (string.IsNullOrEmpty(user.FirstName) && !string.IsNullOrEmpty(givenName))
            {
                user.FirstName = givenName;
                updated = true;
            }
            if (string.IsNullOrEmpty(user.LastName) && !string.IsNullOrEmpty(familyName))
            {
                user.LastName = familyName;
                updated = true;
            }
            if (updated)
            {
                user.UpdatedAt = DateTimeOffset.UtcNow;
                await userStore.UpdateAsync(user, ct);
            }
        }

        // Check if account is active
        if (!user.IsActive)
        {
            logger.LogWarning("OIDC login denied for deactivated user {UserId} ({Email})", user.Id, email);
            return RedirectWithError(returnUrl, "account_disabled", "Account has been deactivated.");
        }

        // Establish the federated identity link on first sign-in (provider/providerKey resolved above).
        if (existingLogin is null)
        {
            await userStore.AddLoginAsync(new ExternalLoginInfo
            {
                UserId = user.Id,
                Provider = provider,
                ProviderKey = providerKey,
                DisplayName = config.ConnectionName
            }, ct);

            logger.LogInformation("Linked external login {Provider}:{ProviderKey} to user {UserId}",
                provider, providerKey, user.Id);
        }

        // F42: federation proves the FIRST factor only. If the user's effective policy requires MFA (they
        // are enrolled, or the client mandates it), route through the local MFA challenge/setup instead of
        // signing a fully-authenticated session — otherwise a bare federated login silently satisfies a
        // tenant's MFA requirement. When MFA is neither enrolled nor required, federation stands alone.
        // Per-connection override: the tenant may trust the IdP's own MFA as the second factor,
        // in which case the local challenge is skipped and federation signs in mfa-authenticated.
        // The sid and every federation binding are settled HERE, above the MFA decision, because the MFA
        // branch returns a redirect and the session is then established by /api/auth/mfa/verify. They used to
        // be built below this point, so parking on a challenge discarded all of them — and the sid in
        // particular, which the upstream refresh token is stored under. See PendingFederatedSession.
        var sessionId = Guid.NewGuid().ToString("N");
        var federationClaims = new List<Claim>();

        // If the connection configures a session-cap claim, carry the upstream value through as
        // "session_max_exp" (Unix seconds). AuthorizeEndpoint reads this and persists it onto
        // the auth code so refresh tokens cannot outlive the federated session.
        DateTimeOffset? idpSessionBound = null;
        if (!string.IsNullOrWhiteSpace(config.SessionExpClaim))
        {
            var sessionExp = ReadUnixSecondsClaim(validationResult.Claims, config.SessionExpClaim);
            if (sessionExp is { } exp)
            {
                federationClaims.Add(new Claim("session_max_exp", exp.ToString()));
                idpSessionBound = DateTimeOffset.FromUnixTimeSeconds(exp);
            }
            else
            {
                logger.LogWarning(
                    "OIDC connection {ConnectionId} configured SessionExpClaim '{Claim}' but claim was missing or unparseable",
                    stateData.ConnectionId, config.SessionExpClaim);
            }
        }

        // Upstream-federated refresh (Option A): carry the upstream refresh token on the cookie so the
        // FIRST /connect/authorize can lift it onto the subject and persist it into the refresh grant
        // (same callback→authorize carrier the session_max_exp / federated:* claims use — no refresh
        // grant exists yet at this callback). NEVER emitted into a token: the resolver copies it to a
        // non-emitted OidcSubject field, and it's redeemed server-to-server on refresh. The cookie is
        // encrypted + httpOnly. Only when the connection opts in AND the upstream actually issued one.
        if (config.RevalidateOnRefresh && !string.IsNullOrEmpty(upstreamRefreshToken))
        {
            federationClaims.Add(new Claim("upstream_refresh_token", upstreamRefreshToken));
            federationClaims.Add(new Claim("upstream_connection_id", stateData.ConnectionId));

            // Seed the durable per-(user, connection, sid) store — the authoritative rotating copy every RP
            // grant reads and rotates, instead of each grant pinning this login-time cookie copy (which dies
            // once the upstream one-time-rotates it). No store registered (host without an Azure/AWS provider)
            // → the cookie copy is the fallback. Bounded by the absolute 7-day session cap.
            //
            // Seeded under the sid settled above, which the MFA sign-in now reuses — it used to mint a fresh
            // one, leaving this record unreachable for every federated user who had MFA.
            var upstreamTokenStore = httpContext.RequestServices.GetService<IUpstreamRefreshTokenStore>();
            if (upstreamTokenStore is not null)
                await upstreamTokenStore.SetAsync(user.Id, stateData.ConnectionId, sessionId, upstreamRefreshToken,
                    DateTimeOffset.UtcNow.AddDays(7), ct);
        }

        // Federation claim flow-through: every non-protocol upstream id_token claim
        // rides through as `federated:<name>` on the cookie, so when this user later
        // hits /connect/authorize the resolver can promote them into OidcSubject.CustomAttributes
        // and ProtocolTokenService's scope-gated emission re-releases them on Authagonal-issued
        // tokens. Scope is the only switch — no per-connection allowlist.
        foreach (var (claimName, claimValue) in validationResult.Claims)
        {
            if (FederationProtocolReservedClaims.Contains(claimName))
                continue;
            var stringValue = ConvertClaimValueToString(claimValue);
            if (stringValue is null)
                continue;
            federationClaims.Add(new Claim($"federated:{claimName}", stringValue));
        }

        var loginAppBase = configuration["LoginAppUrl"] ?? "/login";
        if (config.ChallengeMfaAfterLogin)
        {
            var mfaRedirect = await FederatedMfaFlow.MaybeChallengeAsync(
                user,
                httpContext, returnUrl, loginAppBase, clientStore, mfaStore, webAuthnService, authHooks, authOptions.Value, logger, ct,
                grantStore, federationClaims, idpSessionBound, sessionId);
            if (mfaRedirect is not null)
                return mfaRedirect;
        }

        // Sign in with cookie auth
        var displayName = $"{user.FirstName} {user.LastName}".Trim();
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id),
            new("sub", user.Id),
            new(ClaimTypes.Email, user.Email),
            new(ClaimTypes.Name, string.IsNullOrWhiteSpace(displayName) ? user.Email : displayName),
            new("security_stamp", user.SecurityStamp ?? ""),
            new("sid", sessionId),
            // Federation satisfies the local MFA requirement — the upstream IdP owns authentication.
            new(CookieSignInHelper.MfaAuthenticatedClaim, "true")
        };

        if (!string.IsNullOrWhiteSpace(user.OrganizationId))
            claims.Add(new Claim("org_id", user.OrganizationId));

        // The same list the MFA branch parks — assembled once, above.
        claims.AddRange(federationClaims);

        claims.Add(new Claim(CookieSignInHelper.AuthTimeClaim, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()));

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        // Run the onUserAuthenticated hook BEFORE establishing the session, so an enforced hook that
        // rejects the login prevents the cookie from being issued (not a 500 after it's already set).
        await authHooks.RunOnUserAuthenticatedAsync(user.Id, email, "oidc", ct: ct);

        await httpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);

        logger.LogInformation("User {UserId} ({Email}) signed in via OIDC connection {ConnectionId}",
            user.Id, email, stateData.ConnectionId);

        return Results.Redirect(returnUrl);
    }

    private static async Task<(string IdToken, string? AccessToken, string? RefreshToken)> ExchangeCodeForTokensAsync(
        IHttpClientFactory httpClientFactory,
        string tokenEndpoint,
        string code,
        string redirectUri,
        string clientId,
        string clientSecret,
        string codeVerifier,
        CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient("OidcDiscovery");

        var requestBody = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["code_verifier"] = codeVerifier
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
        {
            Content = new FormUrlEncodedContent(requestBody)
        };

        using var response = await client.SendAsync(request, ct);

        var responseBody = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Token exchange failed with status {response.StatusCode}: {responseBody}");
        }

        using var tokenDoc = JsonDocument.Parse(responseBody);
        var root = tokenDoc.RootElement;

        var idToken = root.GetProperty("id_token").GetString()
            ?? throw new InvalidOperationException("Token response missing id_token");

        string? accessToken = null;
        if (root.TryGetProperty("access_token", out var accessTokenElement))
            accessToken = accessTokenElement.GetString();

        string? refreshToken = null;
        if (root.TryGetProperty("refresh_token", out var refreshTokenElement))
            refreshToken = refreshTokenElement.GetString();

        return (idToken, accessToken, refreshToken);
    }

    private static async Task<Dictionary<string, object?>> FetchUserinfoAsync(
        IHttpClientFactory httpClientFactory,
        string userinfoEndpoint,
        string accessToken,
        CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient("OidcDiscovery");

        using var request = new HttpRequestMessage(HttpMethod.Get, userinfoEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);

        var claims = new Dictionary<string, object?>();
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            claims[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => prop.Value.GetDouble(),
                _ => prop.Value.GetRawText()
            };
        }

        return claims;
    }

    private static string? Claim(IDictionary<string, object> claims, string key)
        => claims.TryGetValue(key, out var v) ? v as string : null;

    private static object? ClaimObj(IDictionary<string, object> claims, string key)
        => claims.TryGetValue(key, out var v) ? v : null;

    /// <summary>
    /// Read a numeric (or numeric-string) claim as Unix seconds. Returns null when absent or
    /// unparseable — JsonWebTokenHandler may surface a numeric JSON claim as long/int/string
    /// depending on its parse path, so accept each shape.
    /// </summary>
    private static long? ReadUnixSecondsClaim(IDictionary<string, object> claims, string key)
    {
        if (!claims.TryGetValue(key, out var raw) || raw is null)
            return null;

        return raw switch
        {
            long l => l,
            int i => i,
            double d => (long)d,
            string s when long.TryParse(s, out var parsed) => parsed,
            _ => null
        };
    }

    private static string? ExtractEmail(IDictionary<string, object> claims)
    {
        if (Claim(claims, "email") is { Length: > 0 } email)
            return email;

        if (ClaimObj(claims, "emails") is string emailsStr)
        {
            try
            {
                using var doc = JsonDocument.Parse(emailsStr);
                if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
                    return doc.RootElement[0].GetString();
            }
            catch
            {
                return emailsStr;
            }
        }

        if (ClaimObj(claims, "emails") is JsonElement emailsElement)
        {
            if (emailsElement.ValueKind == JsonValueKind.Array && emailsElement.GetArrayLength() > 0)
                return emailsElement[0].GetString();
            if (emailsElement.ValueKind == JsonValueKind.String)
                return emailsElement.GetString();
        }

        return null;
    }

    private static string? ExtractEmailFromJson(Dictionary<string, object?> claims)
    {
        if (claims.TryGetValue("email", out var emailObj) && emailObj is string email && email.Length > 0)
            return email;

        if (claims.TryGetValue("emails", out var emailsObj) && emailsObj is string emailsStr)
        {
            try
            {
                using var doc = JsonDocument.Parse(emailsStr);
                if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
                    return doc.RootElement[0].GetString();
            }
            catch
            {
                return emailsStr;
            }
        }

        return null;
    }

    /// <summary>See <see cref="Authagonal.Core.Services.LocalRedirect"/> — one shared implementation, and it
    /// rejects the control characters that defeated every local copy of this check.</summary>
    private static string SanitizeReturnUrl(string? url)
        => Authagonal.Core.Services.LocalRedirect.Sanitize(url, "/login");

    // F48c: appends the OAuth error to returnUrl with the correct separator. returnUrl is the original
    // /authorize URL, which already carries a query string — a naive "?error=" produced a malformed
    // double-"?" that swallowed the error params.
    private static IResult RedirectWithError(string returnUrl, string error, string description)
    {
        var sep = returnUrl.Contains('?') ? '&' : '?';
        return Results.Redirect($"{returnUrl}{sep}error={Uri.EscapeDataString(error)}&error_description={Uri.EscapeDataString(description)}");
    }

    /// <summary>
    /// Claim names we never propagate as federation claims because they're protocol
    /// machinery (issuer/audience/timestamps), already extracted as identity (sub,
    /// email, name fields), or already handled separately (session_max_exp, sid).
    /// Anything else flows through scope-gated emission.
    /// </summary>
    private static readonly HashSet<string> FederationProtocolReservedClaims = new(StringComparer.Ordinal)
    {
        "iss", "sub", "aud", "exp", "nbf", "iat", "jti",
        "scope", "client_id", "nonce", "auth_time", "acr", "amr", "sid",
        "at_hash", "c_hash", "azp",
        "email", "email_verified", "name", "given_name", "family_name", "phone_number",

        // Claims this server derives itself and must not accept an upstream's word for.
        //
        // Federation claims are merged with overwriteExisting: true — deliberately, since they
        // describe the authoritative state of the upstream-issued session — so an upstream id_token
        // carrying one of these would have replaced the server's own value. org_id decides tenancy;
        // roles and groups are authorization inputs resolved from this server's stores; and
        // federated_connection is the marker recording WHICH upstream an account came from, so an
        // upstream asserting it could name a different, more-trusted connection than its own.
        "org_id", "roles", "groups", "federated_connection",
    };

    private static string? ConvertClaimValueToString(object? value)
    {
        return value switch
        {
            null => null,
            string s => s,
            bool b => b ? "true" : "false",
            int or long or double or float or decimal => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture),
            JsonElement el => el.ValueKind switch
            {
                JsonValueKind.String => el.GetString(),
                JsonValueKind.Number => el.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Null => null,
                _ => el.GetRawText(),
            },
            _ => value.ToString(),
        };
    }

    /// <summary>
    /// Collects whitelisted passthrough query values to forward to the upstream IdP.
    /// Tries the returnUrl's query string first (canonical — that's the original
    /// /authorize request), then the LoginAsync request's own query as a fallback.
    /// Whitelist keys are matched ordinally; missing keys are silently skipped.
    /// </summary>
    private static IEnumerable<KeyValuePair<string, string>> CollectPassthroughs(
        IReadOnlyList<string> whitelist,
        string? returnUrl,
        IQueryCollection currentQuery)
    {
        System.Collections.Specialized.NameValueCollection? returnUrlQuery = null;
        if (!string.IsNullOrWhiteSpace(returnUrl))
        {
            var queryStart = returnUrl.IndexOf('?');
            if (queryStart >= 0)
                returnUrlQuery = System.Web.HttpUtility.ParseQueryString(returnUrl[queryStart..]);
        }

        foreach (var key in whitelist)
        {
            var value = returnUrlQuery?[key];
            if (string.IsNullOrEmpty(value))
                value = currentQuery[key].FirstOrDefault();
            if (string.IsNullOrEmpty(value)) continue;
            yield return new KeyValuePair<string, string>(key, value);
        }
    }

    /// <summary>Standard OIDC scopes safe to forward to ANY upstream IdP. Anything else the downstream RP
    /// requested (custom API scopes, <c>offline_access</c>, …) is dropped — a strict IdP like Google 400s
    /// <c>invalid_scope</c> on unknown values (F40), and the upstream only needs to identify the user. The
    /// downstream's own scopes are re-released on Authagonal-issued tokens via the federation claim
    /// flow-through, not by the upstream.</summary>
    private static readonly HashSet<string> StandardUpstreamScopes = new(StringComparer.Ordinal)
    {
        "openid", "profile", "email", "address", "phone",
    };

    /// <summary>
    /// Pulls the <c>scope</c> query parameter off the original /authorize URL we were
    /// asked to bring the user back to after federation, filtered to the standard OIDC set
    /// (see <see cref="StandardUpstreamScopes"/>). Returns null if returnUrl doesn't carry one
    /// (e.g. login UI redirected here directly with returnUrl="/"). Always ensures <c>openid</c>.
    /// </summary>
    private static string? ExtractScopeFromReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl)) return null;

        var queryStart = returnUrl.IndexOf('?');
        if (queryStart < 0) return null;

        var query = System.Web.HttpUtility.ParseQueryString(returnUrl[queryStart..]);
        var scope = query["scope"];
        if (string.IsNullOrWhiteSpace(scope)) return null;

        var scopes = scope.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(StandardUpstreamScopes.Contains)
            .ToList();
        if (!scopes.Contains("openid", StringComparer.Ordinal))
            scopes.Add("openid");

        return string.Join(' ', scopes);
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
