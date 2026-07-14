using System.Security.Claims;
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

    public static IEndpointRouteBuilder MapSamlEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/saml/{connectionId}/login", LoginAsync).AllowAnonymous();
        app.MapPost("/saml/{connectionId}/acs", AcsAsync).AllowAnonymous().DisableAntiforgery();
        app.MapGet("/saml/{connectionId}/metadata", MetadataAsync).AllowAnonymous();

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
        IProvisioningOrchestrator provisioning,
        IConfiguration configuration,
        IOptions<AuthOptions> authOptions,
        IOptions<CacheOptions> cacheOptions,
        ILogger<Program> logger,
        CancellationToken ct)
    {
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
            // Quick parse to find InResponseTo without full validation
            var responseBytes = Convert.FromBase64String(samlResponse);
            var responseXml = System.Text.Encoding.UTF8.GetString(responseBytes);
            var quickDoc = new System.Xml.XmlDocument { XmlResolver = null };
            quickDoc.LoadXml(responseXml);
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
                logger.LogWarning("SAML replay detected or unknown request ID: InResponseTo={InResponseTo}", expectedInResponseTo);
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

        // Build validation context
        var validationContext = new SamlResponseValidationContext(
            ExpectedAcsUrl: acsUrl,
            ExpectedAudience: config.EntityId,
            ExpectedInResponseTo: expectedInResponseTo,
            TrustedCertificates: metadata.SigningCertificates);

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

        var isNewAssertion = await replayCache.CheckAndStoreAssertionIdAsync(parseResult.AssertionId, ct);
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
            if (config.DisableJitProvisioning)
            {
                logger.LogInformation("JIT provisioning disabled for SAML connection {ConnectionId}, rejecting unknown user {Email}", connectionId, email);
                return RedirectWithError(relayState, "access_denied", "User not found. Contact your administrator to be provisioned.");
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
        var loginAppBase = configuration["LoginAppUrl"] ?? "/login";
        var mfaRedirect = await FederatedMfaFlow.MaybeChallengeAsync(
            user, relayState, loginAppBase, clientStore, mfaStore, webAuthnService, authHooks, authOptions.Value, logger, ct);
        if (mfaRedirect is not null)
            return mfaRedirect;

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

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        // Run the onUserAuthenticated hook BEFORE establishing the session, so an enforced hook that
        // rejects the login prevents the cookie from being issued (not a 500 after it's already set).
        await authHooks.RunOnUserAuthenticatedAsync(user.Id, email, "saml", ct: ct);

        await httpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);

        logger.LogInformation("User {UserId} ({Email}) signed in via SAML connection {ConnectionId}",
            user.Id, email, connectionId);

        // Redirect to RelayState (already sanitized)
        return Results.Redirect(relayState);
    }

    private static async Task<IResult> MetadataAsync(
        string connectionId,
        ISamlProviderStore samlStore,
        Authagonal.Core.Services.ITenantContext tenantContext,
        CancellationToken ct)
    {
        var config = await samlStore.GetAsync(connectionId, ct);
        if (config is null)
            return Results.NotFound(new { error = "not_found", error_description = $"SAML connection '{connectionId}' not found" });

        var baseUrl = tenantContext.Issuer;
        var acsUrl = $"{baseUrl}/saml/{connectionId}/acs";
        var issuer = config.EntityId;

        // F51: advertise the connection's requested NameID format; omit the element when the
        // connection omits NameIDPolicy ("none") — advertising a format we don't request misleads.
        var nameIdFormatLine = string.Equals(config.NameIdFormat, Services.Saml.SamlRequestBuilder.NameIdFormatNone, StringComparison.OrdinalIgnoreCase)
            ? ""
            : $"""
                <md:NameIDFormat>{System.Security.SecurityElement.Escape(config.NameIdFormat ?? Services.Saml.SamlConstants.NameIdEmail)}</md:NameIDFormat>
            """;

        var xml = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <md:EntityDescriptor xmlns:md="urn:oasis:names:tc:SAML:2.0:metadata"
                entityID="{issuer}">
              <md:SPSSODescriptor
                  AuthnRequestsSigned="false"
                  WantAssertionsSigned="true"
                  protocolSupportEnumeration="urn:oasis:names:tc:SAML:2.0:protocol">{nameIdFormatLine}
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

    private static string SanitizeReturnUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "/";

        // Must be a same-site relative path. Reject anything a browser could read as an authority:
        // "//host", a leading "/\", or any embedded backslash (WHATWG treats '\' as '/', so "/\evil.com"
        // navigates off-site). RelayState is attacker-controllable, so this is load-bearing. See F37.
        if (!url.StartsWith('/') || url.StartsWith("//") || url.Contains('\\'))
            return "/";

        return url;
    }

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
