using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.Server.Services;
using Authagonal.Server.Services.Saml;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Authagonal.Server.Endpoints;

public static class SamlEndpoints
{
    /// <summary>
    /// Cap on the decoded SAML response. A real assertion is a few KB; ASP.NET Core's default form limit
    /// would otherwise permit ~4 MB of attacker XML to reach the parser on an anonymous endpoint.
    /// </summary>
    private const int MaxSamlResponseBytes = 512 * 1024;


    public static IEndpointRouteBuilder MapSamlEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/saml/{connectionId}/login", LoginAsync).AllowAnonymous();
        app.MapPost("/saml/{connectionId}/acs", AcsAsync).AllowAnonymous().DisableAntiforgery();
        app.MapGet("/saml/{connectionId}/metadata", MetadataAsync).AllowAnonymous();
        // F55: single logout. /logout starts SP-initiated SLO for the current session; /slo receives
        // IdP-initiated LogoutRequests and the LogoutResponse leg of SP-initiated SLO.
        app.MapGet("/saml/{connectionId}/logout", LogoutAsync).AllowAnonymous();
        app.MapGet("/saml/{connectionId}/slo", SloAsync).AllowAnonymous();
        app.MapPost("/saml/{connectionId}/slo", SloAsync).AllowAnonymous().DisableAntiforgery();

        return app;
    }

    private static async Task<IResult> LoginAsync(
        string connectionId,
        string? returnUrl,
        string? loginHint,
        ISamlProviderStore samlStore,
        SamlMetadataParser metadataParser,
        Authagonal.Core.Services.ISamlReplayCache replayCache,
        IMemoryCache memoryCache,
        Authagonal.Core.Services.ITenantContext tenantContext,
        Authagonal.Core.Services.ISecretProvider secretProvider,
        IOptions<CacheOptions> cacheOptions,
        ILogger<Program> logger,
        CancellationToken ct)
    {
        var config = await samlStore.GetAsync(connectionId, ct);
        if (config is null)
            return Results.NotFound(new { error = "not_found", error_description = $"SAML connection '{connectionId}' not found" });

        // Parse IdP metadata (cached)
        var metadata = await GetCachedMetadataAsync(config, metadataParser, memoryCache, cacheOptions.Value, ct);

        // Generate request ID
        var requestId = "_" + Guid.NewGuid().ToString("N");

        // Store request ID in replay cache, carrying the post-login return URL server-side. F56:
        // the SAML spec caps RelayState at 80 bytes and some IdPs truncate it — a full /authorize
        // returnUrl doesn't fit, so it rides the request row and comes back via InResponseTo.
        await replayCache.StoreRequestAsync(requestId, connectionId, SanitizeReturnUrl(returnUrl), ct);

        // Build the issuer (our entity ID)
        var issuer = config.EntityId;
        var baseUrl = tenantContext.Issuer;
        var acsUrl = $"{baseUrl}/saml/{connectionId}/acs";

        // Build redirect URL (no RelayState — the return URL is server-side state now)
        var url = SamlRequestBuilder.BuildAuthnRequestUrl(
            requestId, issuer, acsUrl, metadata.SingleSignOnServiceUrl, loginHint, config.NameIdFormat);

        // F54: sign the redirect binding when we have an SP key and either the connection forces it
        // or the IdP's metadata declares WantAuthnRequestsSigned (ADFS configured for signed requests).
        if (!string.IsNullOrEmpty(config.SpCertificate) &&
            (config.SignAuthnRequests == true || metadata.WantAuthnRequestsSigned))
        {
            using var spCert = SamlSpKey.Load(await secretProvider.ResolveAsync(config.SpCertificate, ct));
            using var rsa = spCert.GetRSAPrivateKey();
            if (rsa is not null)
                url = SamlRedirectBinding.Sign(url, rsa);
        }

        logger.LogInformation("SAML login initiated for connection {ConnectionId}, RequestId={RequestId}",
            connectionId, requestId);

        return Results.Redirect(url);
    }

    private static async Task<IResult> AcsAsync(
        string connectionId,
        HttpContext httpContext,
        ISamlProviderStore samlStore,
        IUserStore userStore,
        IClientStore clientStore,
        IMfaStore mfaStore,
        WebAuthnService webAuthnService,
        IEnumerable<IAuthHook> authHooks,
        SamlMetadataParser metadataParser,
        SamlResponseParser responseParser,
        Authagonal.Core.Services.ISamlReplayCache replayCache,
        IMemoryCache memoryCache,
        Authagonal.Core.Services.ITenantContext tenantContext,
        Authagonal.Core.Services.ISecretProvider secretProvider,
        IProvisioningOrchestrator provisioning,
        IConfiguration configuration,
        IOptions<AuthOptions> authOptions,
        IOptions<CacheOptions> cacheOptions,
        Authagonal.Core.Services.IRateLimiter rateLimiter,
        ILogger<Program> logger,
        CancellationToken ct)
    {
        // The ACS is anonymous, antiforgery-disabled and does real cryptographic work per request
        // (RSA unwrap + symmetric decrypt + signature verification). Unthrottled, it is both a CPU
        // amplifier and the request budget an adaptive chosen-ciphertext attack spends — the padding-oracle
        // and Bleichenbacher attacks on the EncryptedAssertion path need ~10^4-10^5 probes, so a per-source
        // ceiling is a real cost increase even with the error responses now collapsed to one constant.
        // Keyed on the raw peer, not a forwarded header, so it cannot be evaded by spoofing X-Forwarded-For.
        var acsPeer = Services.Cluster.InternalEndpointGuard.RawPeerAddress(httpContext)?.ToString() ?? "unknown";
        if (await rateLimiter.IsRateLimitedAsync($"saml-acs|{connectionId}|{acsPeer}", 60, TimeSpan.FromMinutes(1), ct))
        {
            logger.LogWarning("SAML ACS rate limit hit for connection {ConnectionId}", connectionId);
            return Results.StatusCode(429);
        }

        // Read form data. RelayState is only meaningful for IdP-initiated flows now (the IdP's
        // configured default RelayState); SP-initiated return URLs ride the stored request row (F56).
        var form = await httpContext.Request.ReadFormAsync(ct);
        var samlResponse = form["SAMLResponse"].ToString();
        var relayState = SanitizeReturnUrl(form["RelayState"].ToString());

        if (string.IsNullOrEmpty(samlResponse))
            return Results.BadRequest(new { error = "missing_saml_response" });

        // Load config
        var config = await samlStore.GetAsync(connectionId, ct);
        if (config is null)
            return Results.NotFound(new { error = "not_found", error_description = $"SAML connection '{connectionId}' not found" });

        // Parse IdP metadata (cached)
        var metadata = await GetCachedMetadataAsync(config, metadataParser, memoryCache, cacheOptions.Value, ct);

        var baseUrl = tenantContext.Issuer;
        var acsUrl = $"{baseUrl}/saml/{connectionId}/acs";

        // Try to extract InResponseTo for replay validation
        string? expectedInResponseTo = null;
        try
        {
            // Quick parse to find InResponseTo before signature validation. Uses the SAME hardened loader
            // as the full parse — this used to be a bare XmlDocument.LoadXml, whose XmlTextReader parses
            // DTDs with no entity-expansion cap, so a ~1 KB document of nested internal entities expanded
            // to gigabytes here before any authentication, signature or replay check ran. XmlResolver =
            // null blocked external entities but not internal expansion.
            var responseBytes = Convert.FromBase64String(samlResponse);
            if (responseBytes.Length > MaxSamlResponseBytes)
            {
                logger.LogWarning("SAML response exceeds {Max} bytes; refusing", MaxSamlResponseBytes);
                return RedirectWithError(relayState, "saml_error", "SAML response is too large.");
            }
            var responseXml = System.Text.Encoding.UTF8.GetString(responseBytes);
            var quickDoc = Services.Saml.SamlResponseParser.LoadHardened(responseXml);
            expectedInResponseTo = quickDoc.DocumentElement?.Attributes?["InResponseTo"]?.Value;
        }
        catch
        {
            // If we can't extract it, proceed without replay validation
            logger.LogWarning("Could not extract InResponseTo from SAML response for replay validation");
        }

        // Validate replay cache if we have an InResponseTo.
        // IdP-initiated flows have no InResponseTo and skip this block entirely.
        // If InResponseTo IS present, replay validation must pass — reject otherwise.
        if (expectedInResponseTo is not null)
        {
            var requestState = await replayCache.ValidateAndConsumeRequestAsync(expectedInResponseTo, ct);
            if (requestState is null)
            {
                // InResponseTo comes off an unauthenticated POST body, so it is logged as a hash
                // rather than verbatim: the raw value can carry CR/LF and forge log entries in any
                // line-oriented sink, and it correlates just as well hashed.
                logger.LogWarning("SAML replay detected or unknown request ID: InResponseTo(sha256)={Digest}",
                    LogSafeDigest(expectedInResponseTo));
                return Results.BadRequest(new { error = "saml_replay", error_description = "SAML response replay detected or unknown request ID." });
            }
            else if (!string.Equals(requestState.ConnectionId, connectionId, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("SAML connection mismatch: expected={Expected}, actual={Actual}",
                    requestState.ConnectionId, connectionId);
                return Results.BadRequest(new { error = "connection_mismatch" });
            }

            // F56: the SP-initiated return URL comes from the stored request row, not RelayState.
            if (!string.IsNullOrEmpty(requestState.ReturnUrl))
                relayState = SanitizeReturnUrl(requestState.ReturnUrl);
        }

        // F54: when the connection carries an SP keypair, hand the parser its private key so an
        // EncryptedAssertion (ADFS default once our metadata advertises an encryption cert) decrypts.
        using var spCert = string.IsNullOrEmpty(config.SpCertificate)
            ? null
            : SamlSpKey.Load(await secretProvider.ResolveAsync(config.SpCertificate, ct));
        using var spDecryptionKey = spCert?.GetRSAPrivateKey();

        // Build validation context
        var validationContext = new SamlResponseValidationContext(
            ExpectedAcsUrl: acsUrl,
            ExpectedAudience: config.EntityId,
            ExpectedInResponseTo: expectedInResponseTo,
            TrustedCertificates: metadata.SigningCertificates,
            // From the IdP's own metadata — the trust anchor's declaration of who it is — rather than
            // a separately-configured value that could drift from it. Null-safe: metadata without an
            // entityID leaves the previous behaviour rather than failing every login on upgrade.
            ExpectedIssuer: metadata.EntityId,
            DecryptionKey: spDecryptionKey);

        // Parse and validate the response
        var parseResult = responseParser.Parse(samlResponse, validationContext);

        // F52: a signature failure right after an IdP cert rollover means our cached metadata is
        // stale (the new cert was published after our last fetch). Evict, refetch once, and retry
        // validation — rate-limited per metadata location so a garbage assertion can't be used to
        // hammer the IdP's metadata endpoint. Without this, rollover = failed logins per pod until
        // the cache TTL lapses.
        if (!parseResult.Success &&
            parseResult.Error == SamlResponseParser.SignatureFailure &&
            !string.IsNullOrWhiteSpace(config.MetadataLocation) &&
            string.IsNullOrWhiteSpace(config.MetadataXml))
        {
            var cooldownKey = $"saml-metadata-refetch:{config.MetadataLocation}";
            if (!memoryCache.TryGetValue(cooldownKey, out _))
            {
                memoryCache.Set(cooldownKey, true, TimeSpan.FromMinutes(5));
                memoryCache.Remove($"saml-metadata:{config.MetadataLocation}");
                try
                {
                    metadata = await GetCachedMetadataAsync(config, metadataParser, memoryCache, cacheOptions.Value, ct);
                    validationContext = validationContext with { TrustedCertificates = metadata.SigningCertificates };
                    parseResult = responseParser.Parse(samlResponse, validationContext);
                    logger.LogInformation("SAML metadata refetched after signature failure for connection {ConnectionId}; retry {Outcome}",
                        connectionId, parseResult.Success ? "succeeded (IdP cert rollover)" : "still failing");
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "SAML metadata refetch after signature failure failed for connection {ConnectionId}", connectionId);
                }
            }
        }

        if (!parseResult.Success)
        {
            logger.LogWarning("SAML response validation failed: {Error}", parseResult.Error);
            return RedirectWithError(relayState, "saml_error", parseResult.Error ?? "Unknown error");
        }

        // The SIGNED InResponseTo decides whether this was SP-initiated — not the Response wrapper's
        // copy, which sits outside the signature whenever the IdP signs only the assertion (the common
        // configuration). Deleting that one unsigned attribute from a captured response turned an
        // SP-initiated response into one accepted as "IdP-initiated", which skipped request validation
        // and replay consumption entirely, while the signature still verified.
        //
        // So an assertion carrying InResponseTo must always be matched to a real outstanding request,
        // regardless of what the wrapper said.
        if (parseResult.SignedInResponseTo is { Length: > 0 } signedInResponseTo
            && expectedInResponseTo is null)
        {
            var signedRequestState = await replayCache.ValidateAndConsumeRequestAsync(signedInResponseTo, ct);
            if (signedRequestState is null)
            {
                logger.LogWarning(
                    "SAML assertion carries a signed InResponseTo with no matching outstanding request " +
                    "(the Response wrapper's copy was absent — likely stripped): {InResponseTo}",
                    signedInResponseTo);
                return Results.BadRequest(new { error = "saml_replay", error_description = "SAML response replay detected or unknown request ID." });
            }

            if (!string.Equals(signedRequestState.ConnectionId, connectionId, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("SAML connection mismatch on the signed InResponseTo: expected={Expected}, actual={Actual}",
                    signedRequestState.ConnectionId, connectionId);
                return Results.BadRequest(new { error = "connection_mismatch" });
            }

            if (!string.IsNullOrEmpty(signedRequestState.ReturnUrl))
                relayState = SanitizeReturnUrl(signedRequestState.ReturnUrl);
        }

        // Enforce assertion single-use for EVERY accepted assertion, regardless of flow.
        // The SP-initiated request-ID check above keys off the <Response> InResponseTo attribute,
        // which is NOT covered when an IdP signs only the <Assertion> (the common case). An attacker
        // who captures such a response can strip the unsigned InResponseTo to reach the
        // "IdP-initiated" branch and replay it. Storing the assertion ID unconditionally closes that:
        // the ID lives inside the signed assertion, so it cannot be altered without breaking the
        // signature, and a second presentation of the same assertion is rejected.
        if (string.IsNullOrEmpty(parseResult.AssertionId))
        {
            logger.LogWarning("SAML assertion has no ID; cannot guarantee single-use. ConnectionId={ConnectionId}", connectionId);
            return Results.BadRequest(new { error = "saml_invalid", error_description = "SAML assertion is missing an ID." });
        }

        // Namespaced per connection: the assertion ID space belongs to the issuing IdP, so a global
        // namespace let one IdP's assertion ID collide with another's and be rejected as a replay —
        // and, on a multi-tenant host, let one tenant's traffic deny another's by ID collision.
        var isNewAssertion = await replayCache.CheckAndStoreAssertionIdAsync(
            $"{connectionId}|{parseResult.AssertionId}", parseResult.AcceptableUntil, ct);
        if (!isNewAssertion)
        {
            logger.LogWarning("SAML assertion replay detected: AssertionId={AssertionId}, ConnectionId={ConnectionId}",
                parseResult.AssertionId, connectionId);
            return Results.BadRequest(new { error = "saml_replay", error_description = "SAML assertion replay detected." });
        }

        // Map claims
        var userInfo = SamlClaimMapper.MapClaims(
            parseResult.NameId!, parseResult.NameIdFormat, parseResult.Attributes, parseResult.AttributeValues);

        if (string.IsNullOrEmpty(userInfo.Email))
        {
            logger.LogWarning("No email address found in SAML response for connection {ConnectionId}", connectionId);
            return RedirectWithError(relayState, "saml_error", "No email address found in SAML assertion.");
        }

        var email = userInfo.Email.ToLowerInvariant();
        var emailDomain = email.Contains('@') ? email[(email.LastIndexOf('@') + 1)..] : "";

        // A SAML assertion has no standard "email verified" flag; the connection's AllowedDomains is
        // the admin's explicit statement of which domains this IdP is authorised to assert. Enforce it
        // (when configured) to stop one connection asserting another's domain or a local user's email.
        var domainAllowed = config.AllowedDomains is { Count: > 0 } &&
            config.AllowedDomains.Any(d => string.Equals(d, emailDomain, StringComparison.OrdinalIgnoreCase));
        if (config.AllowedDomains is { Count: > 0 } && !domainAllowed)
        {
            logger.LogWarning("SAML email domain '{Domain}' not permitted for connection {ConnectionId}", emailDomain, connectionId);
            return RedirectWithError(relayState, "access_denied", "Your email domain is not permitted for this connection.");
        }

        // Resolve a returning user by their STABLE federated identity (provider + NameID) first —
        // never by email alone.
        // F50: a transient NameID rotates every login — using it as the federated key would JIT a
        // duplicate user per sign-in. Fall back to the IdP's stable object id when asserted;
        // otherwise reject with an actionable error rather than silently multiplying accounts.
        var provider = $"saml:{connectionId}";
        var providerKey = parseResult.NameId!;
        if (string.Equals(parseResult.NameIdFormat, Services.Saml.SamlConstants.NameIdTransient, StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(userInfo.ObjectId))
            {
                providerKey = userInfo.ObjectId;
            }
            else
            {
                logger.LogWarning("SAML connection {ConnectionId} asserted a transient NameID with no stable object-id attribute", connectionId);
                return RedirectWithError(relayState, "saml_error",
                    "The IdP sent a transient NameID and no stable identifier. Configure a persistent or emailAddress NameID format at your IdP, or assert an object-id attribute.");
            }
        }
        var existingLogin = await userStore.FindLoginAsync(provider, providerKey, ct);
        var user = existingLogin is not null ? await userStore.GetAsync(existingLogin.UserId, ct) : null;

        // Match an existing local account by email only when the connection is explicitly authorised
        // for that email's domain (AllowedDomains vouches the IdP owns it). Without that vouching we
        // refuse to attach this IdP to a pre-existing account, preventing account takeover.
        if (user is null)
        {
            var existingByEmail = await userStore.FindByEmailAsync(email, ct);
            if (existingByEmail is not null)
            {
                if (!domainAllowed)
                {
                    logger.LogWarning("SAML login rejected: email {Email} matches an existing account but connection {ConnectionId} is not authorised for its domain", email, connectionId);
                    return RedirectWithError(relayState, "access_denied", "This email already belongs to an account. Contact your administrator to link it.");
                }
                user = existingByEmail;
            }
        }

        if (user is null)
        {
            // Whitelisted authorize-request params captured from the SP-initiated return URL (relayState =
            // the /authorize URL) to carry the downstream provisioning context onto the JIT user. The
            // downstream provisioner is the gate on the VALUES; only whitelisted keys are kept.
            var provisioningAttributes = CollectProvisioningAttributes(config.ProvisioningAttributeParams, relayState).ToList();

            // Shared JIT gate (see FederationJitPolicy — same decision as the OIDC path).
            switch (FederationJitPolicy.Evaluate(config.JitProvisioningEnabled, config.ProvisioningAttributeParams.Count, provisioningAttributes.Count, config.AllowUninvitedJit))
            {
                case FederationJitPolicy.Decision.RejectJitDisabled:
                    logger.LogInformation("JIT provisioning disabled for SAML connection {ConnectionId}, rejecting unknown user {Email}", connectionId, email);
                    return RedirectWithError(relayState, "access_denied", "User not found. Contact your administrator to be provisioned.");
                case FederationJitPolicy.Decision.RejectInviteRequired:
                    logger.LogInformation("JIT rejected for SAML connection {ConnectionId}: no provisioning context on the request for unknown user {Email}", connectionId, email);
                    return RedirectWithError(relayState, "access_denied", "This login requires an invitation. Contact your administrator.");
            }

            user = new AuthUser
            {
                Id = Guid.NewGuid().ToString("N"),
                Email = email,
                NormalizedEmail = email.ToUpperInvariant(),
                EmailConfirmed = true,
                FirstName = userInfo.FirstName,
                LastName = userInfo.LastName,
                CreatedAt = DateTimeOffset.UtcNow,
                LockoutEnabled = true,
                SecurityStamp = Convert.ToBase64String(
                    System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
            };

            foreach (var kv in provisioningAttributes)
                user.CustomAttributes[kv.Key] = kv.Value;

            // Uninvited SSO auto-provision: tag the user with the connection they federated through so the
            // downstream provisioner can place them in that tenant (bullclip names its SAML connections
            // by org id) rather than creating a new one. Only when there was no invite context.
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
                logger.LogWarning(ex, "Provisioning rejected SAML SSO user {Email}", email);
                return Results.BadRequest(new { error = "provisioning_rejected", message = ex.Message });
            }

            logger.LogInformation("Created new user {UserId} ({Email}) via SAML SSO", user.Id, email);
            await authHooks.RunOnUserCreatedAsync(user.Id, email, "saml", ct);
        }
        else
        {
            // Update name fields if they were empty and we now have them
            var updated = false;
            if (string.IsNullOrEmpty(user.FirstName) && !string.IsNullOrEmpty(userInfo.FirstName))
            {
                user.FirstName = userInfo.FirstName;
                updated = true;
            }
            if (string.IsNullOrEmpty(user.LastName) && !string.IsNullOrEmpty(userInfo.LastName))
            {
                user.LastName = userInfo.LastName;
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
            logger.LogWarning("SAML login denied for deactivated user {UserId} ({Email})", user.Id, email);
            return RedirectWithError(relayState, "account_disabled", "Account has been deactivated.");
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

        // F42: federation proves the FIRST factor only. If the user's effective policy requires MFA, route
        // through the local MFA challenge/setup rather than signing a fully-authenticated session — else a
        // bare SAML login silently satisfies a tenant's MFA requirement. relayState carries them onward.
        // Per-connection override: the tenant may trust the IdP's own MFA as the second factor,
        // in which case the local challenge is skipped and federation signs in mfa-authenticated.
        var loginAppBase = configuration["LoginAppUrl"] ?? "/login";
        if (config.ChallengeMfaAfterLogin)
        {
            var mfaRedirect = await FederatedMfaFlow.MaybeChallengeAsync(
                user, relayState, loginAppBase, clientStore, mfaStore, webAuthnService, authHooks, authOptions.Value, logger, ct);
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
            new("sid", Guid.NewGuid().ToString("N")),
            // Federation satisfies the local MFA requirement — the upstream IdP owns authentication.
            new(CookieSignInHelper.MfaAuthenticatedClaim, "true")
        };

        if (!string.IsNullOrWhiteSpace(user.OrganizationId))
            claims.Add(new Claim("org_id", user.OrganizationId));

        // F55: remember the SAML session so /saml/{id}/logout can build a LogoutRequest (NameID +
        // SessionIndex) and IdP-initiated SLO can be matched to this browser's session.
        claims.Add(new Claim("saml_connection", connectionId));
        claims.Add(new Claim("saml_name_id", parseResult.NameId!));
        if (!string.IsNullOrEmpty(parseResult.NameIdFormat))
            claims.Add(new Claim("saml_name_id_format", parseResult.NameIdFormat));
        if (!string.IsNullOrEmpty(parseResult.SessionIndex))
            claims.Add(new Claim("saml_session_index", parseResult.SessionIndex));

        claims.Add(new Claim(CookieSignInHelper.AuthTimeClaim, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()));

        // The IdP's own session bound, carried onto the cookie as session_max_exp — the same claim the
        // OIDC federation path uses, which the subject resolver already reads and which clamps every
        // access, id and refresh token issued from this session. Without it the local session outlived
        // the authentication behind it: an IdP asserting an eight-hour session was overruled by the
        // local cookie lifetime, which is the opposite of what federating to it means.
        if (parseResult.SessionNotOnOrAfter is { } idpSessionBound)
            claims.Add(new Claim("session_max_exp", idpSessionBound.ToUnixTimeSeconds().ToString()));

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        // Run the onUserAuthenticated hook BEFORE establishing the session, so an enforced hook that
        // rejects the login prevents the cookie from being issued (not a 500 after it's already set).
        await authHooks.RunOnUserAuthenticatedAsync(user.Id, email, "saml", ct: ct);

        // The cookie itself expires no later than the IdP's stated bound.
        var signInProperties = new AuthenticationProperties();
        if (parseResult.SessionNotOnOrAfter is { } cookieBound && cookieBound < DateTimeOffset.UtcNow.AddDays(30))
        {
            signInProperties.ExpiresUtc = cookieBound;
            signInProperties.IsPersistent = true;
        }

        await httpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, signInProperties);

        logger.LogInformation("User {UserId} ({Email}) signed in via SAML connection {ConnectionId}",
            user.Id, email, connectionId);

        // Redirect to RelayState (already sanitized)
        return Results.Redirect(relayState);
    }

    private static async Task<IResult> MetadataAsync(
        string connectionId,
        ISamlProviderStore samlStore,
        Authagonal.Core.Services.ITenantContext tenantContext,
        Authagonal.Core.Services.ISecretProvider secretProvider,
        CancellationToken ct)
    {
        var config = await samlStore.GetAsync(connectionId, ct);
        if (config is null)
            return Results.NotFound(new { error = "not_found", error_description = $"SAML connection '{connectionId}' not found" });

        var baseUrl = tenantContext.Issuer;
        var acsUrl = $"{baseUrl}/saml/{connectionId}/acs";
        var sloUrl = $"{baseUrl}/saml/{connectionId}/slo";
        var issuer = config.EntityId;

        // F51: advertise the connection's requested NameID format; omit the element when the
        // connection omits NameIDPolicy ("none") — advertising a format we don't request misleads.
        var nameIdFormatLine = string.Equals(config.NameIdFormat, Services.Saml.SamlRequestBuilder.NameIdFormatNone, StringComparison.OrdinalIgnoreCase)
            ? ""
            : $"""
                <md:NameIDFormat>{System.Security.SecurityElement.Escape(config.NameIdFormat ?? Services.Saml.SamlConstants.NameIdEmail)}</md:NameIDFormat>
            """;

        // F54: when the connection has an SP keypair, publish its PUBLIC cert as both signing and
        // encryption KeyDescriptors. The encryption descriptor is what makes ADFS start encrypting
        // assertions — which we now decrypt — and the signing one lets IdPs verify our signed
        // AuthnRequests/logout messages.
        var keyDescriptors = "";
        if (!string.IsNullOrEmpty(config.SpCertificate))
        {
            using var spCert = Services.Saml.SamlSpKey.Load(await secretProvider.ResolveAsync(config.SpCertificate, ct));
            var certBase64 = Convert.ToBase64String(spCert.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Cert));
            keyDescriptors = $"""

                <md:KeyDescriptor use="signing">
                  <ds:KeyInfo xmlns:ds="http://www.w3.org/2000/09/xmldsig#">
                    <ds:X509Data><ds:X509Certificate>{certBase64}</ds:X509Certificate></ds:X509Data>
                  </ds:KeyInfo>
                </md:KeyDescriptor>
                <md:KeyDescriptor use="encryption">
                  <ds:KeyInfo xmlns:ds="http://www.w3.org/2000/09/xmldsig#">
                    <ds:X509Data><ds:X509Certificate>{certBase64}</ds:X509Certificate></ds:X509Data>
                  </ds:KeyInfo>
                </md:KeyDescriptor>
            """;
        }

        var authnRequestsSigned = config.SignAuthnRequests == true && !string.IsNullOrEmpty(config.SpCertificate)
            ? "true" : "false";

        var xml = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <md:EntityDescriptor xmlns:md="urn:oasis:names:tc:SAML:2.0:metadata"
                entityID="{issuer}">
              <md:SPSSODescriptor
                  AuthnRequestsSigned="{authnRequestsSigned}"
                  WantAssertionsSigned="true"
                  protocolSupportEnumeration="urn:oasis:names:tc:SAML:2.0:protocol">{keyDescriptors}{nameIdFormatLine}
                <md:SingleLogoutService
                    Binding="urn:oasis:names:tc:SAML:2.0:bindings:HTTP-Redirect"
                    Location="{sloUrl}" />
                <md:SingleLogoutService
                    Binding="urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST"
                    Location="{sloUrl}" />
                <md:AssertionConsumerService
                    Binding="urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST"
                    Location="{acsUrl}"
                    index="0"
                    isDefault="true" />
              </md:SPSSODescriptor>
            </md:EntityDescriptor>
            """;

        return Results.Content(xml, "application/xml");
    }

    /// <summary>
    /// F55: SP-initiated single logout. Ends the local cookie session, then — when the IdP supports
    /// SLO and this browser's session came from this connection — sends a LogoutRequest to the IdP
    /// (redirect binding, signed when the SP has a key). IdPs with no SLO (Google) just get the
    /// local sign-out.
    /// </summary>
    private static async Task<IResult> LogoutAsync(
        string connectionId,
        string? returnUrl,
        HttpContext httpContext,
        ISamlProviderStore samlStore,
        SamlMetadataParser metadataParser,
        Authagonal.Core.Services.ISamlReplayCache replayCache,
        IMemoryCache memoryCache,
        Authagonal.Core.Services.ITenantContext tenantContext,
        Authagonal.Core.Services.ISecretProvider secretProvider,
        IOptions<CacheOptions> cacheOptions,
        ILogger<Program> logger,
        CancellationToken ct)
    {
        var config = await samlStore.GetAsync(connectionId, ct);
        if (config is null)
            return Results.NotFound(new { error = "not_found", error_description = $"SAML connection '{connectionId}' not found" });

        var target = SanitizeReturnUrl(returnUrl);

        var auth = await httpContext.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = auth.Succeeded ? auth.Principal : null;
        var sessionConnection = principal?.FindFirst("saml_connection")?.Value;
        var nameId = principal?.FindFirst("saml_name_id")?.Value;
        var nameIdFormat = principal?.FindFirst("saml_name_id_format")?.Value;
        var sessionIndex = principal?.FindFirst("saml_session_index")?.Value;

        // Always end the local session first — the user asked to log out; IdP SLO is best-effort.
        await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

        if (nameId is null || !string.Equals(sessionConnection, connectionId, StringComparison.OrdinalIgnoreCase))
            return Results.Redirect(target);

        SamlIdpMetadata metadata;
        try
        {
            metadata = await GetCachedMetadataAsync(config, metadataParser, memoryCache, cacheOptions.Value, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SAML SLO: could not load IdP metadata for {ConnectionId}; local sign-out only", connectionId);
            return Results.Redirect(target);
        }
        if (string.IsNullOrEmpty(metadata.SingleLogoutServiceUrl))
            return Results.Redirect(target);

        var requestId = "_" + Guid.NewGuid().ToString("N");
        await replayCache.StoreRequestAsync(requestId, connectionId, target, ct);

        var url = SamlRequestBuilder.BuildLogoutRequestUrl(
            requestId, config.EntityId, metadata.SingleLogoutServiceUrl, nameId, nameIdFormat, sessionIndex);

        if (!string.IsNullOrEmpty(config.SpCertificate))
        {
            using var spCert = SamlSpKey.Load(await secretProvider.ResolveAsync(config.SpCertificate, ct));
            using var rsa = spCert.GetRSAPrivateKey();
            if (rsa is not null)
                url = SamlRedirectBinding.Sign(url, rsa);
        }

        logger.LogInformation("SAML SP-initiated logout for connection {ConnectionId}, RequestId={RequestId}", connectionId, requestId);
        return Results.Redirect(url);
    }

    /// <summary>
    /// F55: the SLO endpoint. Receives IdP-initiated LogoutRequests (redirect GET or POST binding)
    /// and the LogoutResponse leg of SP-initiated SLO. Front-channel only — the message arrives in
    /// the user's browser, so ending the cookie session logs out exactly that browser.
    /// </summary>
    private static async Task<IResult> SloAsync(
        string connectionId,
        HttpContext httpContext,
        ISamlProviderStore samlStore,
        SamlMetadataParser metadataParser,
        Authagonal.Core.Services.ISamlReplayCache replayCache,
        IMemoryCache memoryCache,
        Authagonal.Core.Services.ITenantContext tenantContext,
        Authagonal.Core.Services.ISecretProvider secretProvider,
        IOptions<CacheOptions> cacheOptions,
        ILogger<Program> logger,
        CancellationToken ct)
    {
        var config = await samlStore.GetAsync(connectionId, ct);
        if (config is null)
            return Results.NotFound(new { error = "not_found", error_description = $"SAML connection '{connectionId}' not found" });

        var isPost = HttpMethods.IsPost(httpContext.Request.Method);
        string? samlRequest, samlResponse;
        if (isPost)
        {
            var form = await httpContext.Request.ReadFormAsync(ct);
            samlRequest = form["SAMLRequest"].ToString();
            samlResponse = form["SAMLResponse"].ToString();
        }
        else
        {
            samlRequest = httpContext.Request.Query["SAMLRequest"].ToString();
            samlResponse = httpContext.Request.Query["SAMLResponse"].ToString();
        }

        // LogoutResponse: the IdP answering our SP-initiated LogoutRequest. The session is already
        // gone; consume the request id and land the user on the stored return URL.
        if (!string.IsNullOrEmpty(samlResponse))
        {
            var xml = DecodeSloMessage(samlResponse, isPost, logger);
            var inResponseTo = xml?.DocumentElement?.Attributes?["InResponseTo"]?.Value;
            var state = inResponseTo is null ? null : await replayCache.ValidateAndConsumeRequestAsync(inResponseTo, ct);
            return Results.Redirect(SanitizeReturnUrl(state?.ReturnUrl));
        }

        if (string.IsNullOrEmpty(samlRequest))
            return Results.BadRequest(new { error = "missing_saml_message" });

        // IdP-initiated LogoutRequest.
        var requestXml = DecodeSloMessage(samlRequest, isPost, logger);
        if (requestXml?.DocumentElement is not { LocalName: "LogoutRequest" } logoutRequest)
            return Results.BadRequest(new { error = "saml_invalid", error_description = "Expected a LogoutRequest." });

        var requestIdAttr = logoutRequest.Attributes?["ID"]?.Value;
        if (string.IsNullOrEmpty(requestIdAttr))
            return Results.BadRequest(new { error = "saml_invalid", error_description = "LogoutRequest has no ID." });

        var metadata = await GetCachedMetadataAsync(config, metadataParser, memoryCache, cacheOptions.Value, ct);

        // Authenticate the request: a signature (query-level for the redirect binding, XML for POST)
        // validated against the IdP's certs. Unsigned requests are honored only when this browser's
        // own session belongs to this connection — an unauthenticated attacker can then log out
        // nobody but themselves.
        var signatureValid = isPost
            ? SamlResponseParser.ValidateElementSignature(logoutRequest, metadata.SigningCertificates, logger)
            : SamlRedirectBinding.Verify(httpContext.Request.QueryString.Value ?? "", metadata.SigningCertificates);
        if (!signatureValid)
        {
            var auth = await httpContext.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            var sessionConnection = auth.Succeeded ? auth.Principal?.FindFirst("saml_connection")?.Value : null;
            if (!string.Equals(sessionConnection, connectionId, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("SAML SLO: unsigned LogoutRequest for {ConnectionId} without a matching session — ignored", connectionId);
                return Results.BadRequest(new { error = "saml_invalid", error_description = "LogoutRequest is unsigned and no matching session exists." });
            }
        }

        await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        logger.LogInformation("SAML IdP-initiated logout for connection {ConnectionId}", connectionId);

        if (string.IsNullOrEmpty(metadata.SingleLogoutServiceUrl))
            return Results.Text("Logged out.");

        var responseUrl = SamlRequestBuilder.BuildLogoutResponseUrl(requestIdAttr, config.EntityId, metadata.SingleLogoutServiceUrl);
        if (!string.IsNullOrEmpty(config.SpCertificate))
        {
            using var spCert = SamlSpKey.Load(await secretProvider.ResolveAsync(config.SpCertificate, ct));
            using var rsa = spCert.GetRSAPrivateKey();
            if (rsa is not null)
                responseUrl = SamlRedirectBinding.Sign(responseUrl, rsa);
        }
        return Results.Redirect(responseUrl);
    }

    /// <summary>Decode an SLO message: base64+deflate for the redirect binding, plain base64 for POST.</summary>
    private static System.Xml.XmlDocument? DecodeSloMessage(string value, bool isPost, ILogger logger)
    {
        try
        {
            var xml = isPost
                ? System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(value))
                : SamlRedirectBinding.Inflate(value);
            var doc = new System.Xml.XmlDocument { PreserveWhitespace = true, XmlResolver = null };
            using var reader = System.Xml.XmlReader.Create(new StringReader(xml), new System.Xml.XmlReaderSettings
            {
                DtdProcessing = System.Xml.DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersFromEntities = 0,
            });
            doc.Load(reader);
            return doc;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not decode SAML SLO message");
            return null;
        }
    }

    // Extract the config-whitelisted query params from the SP-initiated return URL (the original
    // /authorize URL) so they can be attached to a JIT user as provisioning CustomAttributes. Only
    // whitelisted keys are captured; the downstream provisioner is the security gate on the values.
    private static IEnumerable<KeyValuePair<string, string>> CollectProvisioningAttributes(
        IReadOnlyList<string> whitelist, string? returnUrl)
    {
        if (whitelist.Count == 0 || string.IsNullOrWhiteSpace(returnUrl)) yield break;
        var queryStart = returnUrl.IndexOf('?');
        if (queryStart < 0) yield break;
        var query = System.Web.HttpUtility.ParseQueryString(returnUrl[queryStart..]);
        foreach (var key in whitelist)
        {
            var value = query[key];
            if (!string.IsNullOrEmpty(value))
                yield return new KeyValuePair<string, string>(key, value);
        }
    }

    /// <summary>
    /// RelayState and returnUrl are attacker-controllable, so this is load-bearing (F37). Delegates to the
    /// one shared implementation — the four local copies had already drifted, and none of them rejected the
    /// ASCII tab that the URL parser strips and Kestrel forwards verbatim, which defeated all of them.
    /// </summary>
    /// <summary>
    /// A short digest, for correlating an attacker-supplied identifier in logs without writing it.
    /// </summary>
    private static string LogSafeDigest(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "(none)";
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private static string SanitizeReturnUrl(string? url) => Authagonal.Core.Services.LocalRedirect.Sanitize(url);

    // F48c: append the error to relayState with the correct separator (relayState is the original
    // /authorize URL and already carries a query, so a naive "?error=" produced a malformed double-"?").
    private static IResult RedirectWithError(string relayState, string error, string description)
    {
        var sep = relayState.Contains('?') ? '&' : '?';
        return Results.Redirect($"{relayState}{sep}error={Uri.EscapeDataString(error)}&error_description={Uri.EscapeDataString(description)}");
    }

    private static async Task<SamlIdpMetadata> GetCachedMetadataAsync(
        SamlProviderConfig config,
        SamlMetadataParser metadataParser,
        IMemoryCache memoryCache,
        CacheOptions cacheOpts,
        CancellationToken ct)
    {
        // F49: pasted metadata (IdPs with no metadata URL — Google Workspace, private-network ADFS)
        // takes precedence over MetadataLocation. Cached content-addressed (hash of the XML), which
        // is inherently poison-proof: identical content parses identically regardless of tenant.
        if (!string.IsNullOrWhiteSpace(config.MetadataXml))
        {
            var xmlHash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(config.MetadataXml)));
            var xmlCacheKey = $"saml-metadata-xml:{xmlHash}";
            if (memoryCache.TryGetValue<SamlIdpMetadata>(xmlCacheKey, out var cachedXml) && cachedXml is not null)
                return cachedXml;

            var parsed = SamlMetadataParser.Parse(config.MetadataXml);
            memoryCache.Set(xmlCacheKey, parsed, TimeSpan.FromMinutes(cacheOpts.SamlMetadataCacheMinutes));
            return parsed;
        }

        // F34: key by the authoritative MetadataLocation, NOT the (attacker-settable, semi-public)
        // connectionId. The process-wide IMemoryCache is shared across every tenant on the pod; keying by
        // connectionId let a malicious tenant create a connection reusing a victim's connectionId with its
        // OWN metadata URL and poison the victim's signing certs (→ forged-assertion takeover). Keyed by URL,
        // each connection's metadata is cached under its own source, so no cross-tenant confusion is possible.
        var cacheKey = $"saml-metadata:{config.MetadataLocation}";
        if (memoryCache.TryGetValue<SamlIdpMetadata>(cacheKey, out var cached) && cached is not null)
            return cached;

        var metadata = await metadataParser.ParseFromUrlAsync(config.MetadataLocation, ct);
        memoryCache.Set(cacheKey, metadata, TimeSpan.FromMinutes(cacheOpts.SamlMetadataCacheMinutes));
        return metadata;
    }
}
