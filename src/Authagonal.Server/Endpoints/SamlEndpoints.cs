using System.Security.Claims;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.Server.Services;
using Authagonal.Server.Services.Saml;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Caching.Memory;
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
        SamlReplayCache replayCache,
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

        // Store request ID in replay cache
        await replayCache.StoreRequestIdAsync(requestId, connectionId, ct);

        // Build the issuer (our entity ID)
        var issuer = config.EntityId;
        var baseUrl = tenantContext.Issuer;
        var acsUrl = $"{baseUrl}/saml/{connectionId}/acs";

        // Build redirect URL
        var url = SamlRequestBuilder.BuildAuthnRequestUrl(
            requestId, issuer, acsUrl, metadata.SingleSignOnServiceUrl, loginHint);

        // Append RelayState if returnUrl is provided (validated to prevent open redirect)
        var relayState = SanitizeReturnUrl(returnUrl);
        url += $"&RelayState={Uri.EscapeDataString(relayState)}";

        logger.LogInformation("SAML login initiated for connection {ConnectionId}, RequestId={RequestId}",
            connectionId, requestId);

        return Results.Redirect(url);
    }

    private static async Task<IResult> AcsAsync(
        string connectionId,
        HttpContext httpContext,
        ISamlProviderStore samlStore,
        IUserStore userStore,
        IEnumerable<IAuthHook> authHooks,
        SamlMetadataParser metadataParser,
        SamlResponseParser responseParser,
        SamlReplayCache replayCache,
        IMemoryCache memoryCache,
        Authagonal.Core.Services.ITenantContext tenantContext,
        IProvisioningOrchestrator provisioning,
        IOptions<CacheOptions> cacheOptions,
        ILogger<Program> logger,
        CancellationToken ct)
    {
        // Read form data
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
            var cachedConnectionId = await replayCache.ValidateAndConsumeAsync(expectedInResponseTo, ct);
            if (cachedConnectionId is null)
            {
                logger.LogWarning("SAML replay detected or unknown request ID: InResponseTo={InResponseTo}", expectedInResponseTo);
                return Results.BadRequest(new { error = "saml_replay", error_description = "SAML response replay detected or unknown request ID." });
            }
            else if (!string.Equals(cachedConnectionId, connectionId, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("SAML connection mismatch: expected={Expected}, actual={Actual}",
                    cachedConnectionId, connectionId);
                return Results.BadRequest(new { error = "connection_mismatch" });
            }
        }

        // Build validation context
        var validationContext = new SamlResponseValidationContext(
            ExpectedAcsUrl: acsUrl,
            ExpectedAudience: config.EntityId,
            ExpectedInResponseTo: expectedInResponseTo,
            TrustedCertificates: metadata.SigningCertificates);

        // Parse and validate the response
        var parseResult = responseParser.Parse(samlResponse, validationContext);
        if (!parseResult.Success)
        {
            logger.LogWarning("SAML response validation failed: {Error}", parseResult.Error);
            return Results.Redirect($"{relayState}?error=saml_error&error_description={Uri.EscapeDataString(parseResult.Error ?? "Unknown error")}");
        }

        // Replay protection for IdP-initiated flows (no InResponseTo).
        // SP-initiated flows are already protected by the request ID replay cache above.
        // For IdP-initiated, check the assertion ID to prevent assertion replay.
        if (expectedInResponseTo is null && !string.IsNullOrEmpty(parseResult.AssertionId))
        {
            var isNew = await replayCache.CheckAndStoreAssertionIdAsync(parseResult.AssertionId, ct);
            if (!isNew)
            {
                logger.LogWarning("SAML IdP-initiated assertion replay detected: AssertionId={AssertionId}, ConnectionId={ConnectionId}",
                    parseResult.AssertionId, connectionId);
                return Results.BadRequest(new { error = "saml_replay", error_description = "SAML assertion replay detected." });
            }
        }

        // Map claims
        var userInfo = SamlClaimMapper.MapClaims(
            parseResult.NameId!, parseResult.NameIdFormat, parseResult.Attributes);

        if (string.IsNullOrEmpty(userInfo.Email))
        {
            logger.LogWarning("No email address found in SAML response for connection {ConnectionId}", connectionId);
            return Results.Redirect($"{relayState}?error=saml_error&error_description={Uri.EscapeDataString("No email address found in SAML assertion.")}");
        }

        var email = userInfo.Email.ToLowerInvariant();

        // Find or create user
        var user = await userStore.FindByEmailAsync(email, ct);
        if (user is null)
        {
            if (config.DisableJitProvisioning)
            {
                logger.LogInformation("JIT provisioning disabled for SAML connection {ConnectionId}, rejecting unknown user {Email}", connectionId, email);
                return Results.Redirect($"{relayState}?error=access_denied&error_description={Uri.EscapeDataString("User not found. Contact your administrator to be provisioned.")}");
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
            return Results.Redirect($"{relayState}?error=account_disabled&error_description={Uri.EscapeDataString("Account has been deactivated.")}");
        }

        // Ensure external login link
        var provider = $"saml:{connectionId}";
        var providerKey = parseResult.NameId!;
        var existingLogin = await userStore.FindLoginAsync(provider, providerKey, ct);
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

        // Sign in with cookie auth
        var displayName = $"{user.FirstName} {user.LastName}".Trim();
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id),
            new("sub", user.Id),
            new(ClaimTypes.Email, user.Email),
            new(ClaimTypes.Name, string.IsNullOrWhiteSpace(displayName) ? user.Email : displayName),
            new("security_stamp", user.SecurityStamp ?? ""),
            new("sid", Guid.NewGuid().ToString("N"))
        };

        if (!string.IsNullOrWhiteSpace(user.OrganizationId))
            claims.Add(new Claim("org_id", user.OrganizationId));

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        await httpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);

        logger.LogInformation("User {UserId} ({Email}) signed in via SAML connection {ConnectionId}",
            user.Id, email, connectionId);

        await authHooks.RunOnUserAuthenticatedAsync(user.Id, email, "saml", ct: ct);

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

        var xml = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <md:EntityDescriptor xmlns:md="urn:oasis:names:tc:SAML:2.0:metadata"
                entityID="{issuer}">
              <md:SPSSODescriptor
                  AuthnRequestsSigned="false"
                  WantAssertionsSigned="true"
                  protocolSupportEnumeration="urn:oasis:names:tc:SAML:2.0:protocol">
                <md:NameIDFormat>urn:oasis:names:tc:SAML:1.1:nameid-format:emailAddress</md:NameIDFormat>
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

        // Only allow relative paths to prevent open redirect
        if (!url.StartsWith('/') || url.StartsWith("//"))
            return "/";

        return url;
    }

    private static async Task<SamlIdpMetadata> GetCachedMetadataAsync(
        SamlProviderConfig config,
        SamlMetadataParser metadataParser,
        IMemoryCache memoryCache,
        CacheOptions cacheOpts,
        CancellationToken ct)
    {
        var cacheKey = $"saml-metadata:{config.ConnectionId}";
        if (memoryCache.TryGetValue<SamlIdpMetadata>(cacheKey, out var cached) && cached is not null)
            return cached;

        var metadata = await metadataParser.ParseFromUrlAsync(config.MetadataLocation, ct);
        memoryCache.Set(cacheKey, metadata, TimeSpan.FromMinutes(cacheOpts.SamlMetadataCacheMinutes));
        return metadata;
    }
}
