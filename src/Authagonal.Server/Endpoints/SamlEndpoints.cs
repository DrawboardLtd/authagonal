using System.Security.Claims;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.Server.Services.Saml;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Caching.Memory;

namespace Authagonal.Server.Endpoints;

public static class SamlEndpoints
{
    private static readonly TimeSpan MetadataCacheDuration = TimeSpan.FromHours(1);

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
        ISamlProviderStore samlStore,
        SamlMetadataParser metadataParser,
        SamlReplayCache replayCache,
        IMemoryCache memoryCache,
        IConfiguration configuration,
        ILogger<Program> logger,
        CancellationToken ct)
    {
        var config = await samlStore.GetAsync(connectionId, ct);
        if (config is null)
            return Results.NotFound(new { error = "not_found", error_description = $"SAML connection '{connectionId}' not found" });

        // Parse IdP metadata (cached)
        var metadata = await GetCachedMetadataAsync(config, metadataParser, memoryCache, ct);

        // Generate request ID
        var requestId = "_" + Guid.NewGuid().ToString("N");

        // Store request ID in replay cache
        await replayCache.StoreRequestIdAsync(requestId, connectionId, ct);

        // Build the issuer (our entity ID)
        var issuer = config.EntityId;
        var baseUrl = configuration["Issuer"]!;
        var acsUrl = $"{baseUrl}/saml/{connectionId}/acs";

        // Build redirect URL
        var url = SamlRequestBuilder.BuildAuthnRequestUrl(
            requestId, issuer, acsUrl, metadata.SingleSignOnServiceUrl);

        // Append RelayState if returnUrl is provided
        var relayState = returnUrl ?? "/";
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
        IUserProvisioningService provisioningService,
        SamlMetadataParser metadataParser,
        SamlResponseParser responseParser,
        SamlReplayCache replayCache,
        IMemoryCache memoryCache,
        IConfiguration configuration,
        ILogger<Program> logger,
        CancellationToken ct)
    {
        // Read form data
        var form = await httpContext.Request.ReadFormAsync(ct);
        var samlResponse = form["SAMLResponse"].ToString();
        var relayState = form["RelayState"].ToString();

        if (string.IsNullOrEmpty(samlResponse))
            return Results.BadRequest(new { error = "missing_saml_response" });

        // Load config
        var config = await samlStore.GetAsync(connectionId, ct);
        if (config is null)
            return Results.NotFound(new { error = "not_found", error_description = $"SAML connection '{connectionId}' not found" });

        // Parse IdP metadata (cached)
        var metadata = await GetCachedMetadataAsync(config, metadataParser, memoryCache, ct);

        var baseUrl = configuration["Issuer"]!;
        var acsUrl = $"{baseUrl}/saml/{connectionId}/acs";

        // Try to extract InResponseTo for replay validation
        string? expectedInResponseTo = null;
        try
        {
            // Quick parse to find InResponseTo without full validation
            var responseBytes = Convert.FromBase64String(samlResponse);
            var responseXml = System.Text.Encoding.UTF8.GetString(responseBytes);
            var quickDoc = new System.Xml.XmlDocument();
            quickDoc.LoadXml(responseXml);
            expectedInResponseTo = quickDoc.DocumentElement?.Attributes?["InResponseTo"]?.Value;
        }
        catch
        {
            // If we can't extract it, proceed without replay validation
            logger.LogWarning("Could not extract InResponseTo from SAML response for replay validation");
        }

        // Validate replay cache if we have an InResponseTo
        if (expectedInResponseTo is not null)
        {
            var cachedConnectionId = await replayCache.ValidateAndConsumeAsync(expectedInResponseTo, ct);
            if (cachedConnectionId is null)
            {
                logger.LogWarning("SAML replay check failed for InResponseTo={InResponseTo}", expectedInResponseTo);
                // Don't hard-fail here — some IdP-initiated flows won't have InResponseTo
                expectedInResponseTo = null;
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
            var errorRedirect = string.IsNullOrEmpty(relayState) ? "/" : relayState;
            return Results.Redirect($"{errorRedirect}?error=saml_error&error_description={Uri.EscapeDataString(parseResult.Error ?? "Unknown error")}");
        }

        // Map claims
        var userInfo = SamlClaimMapper.MapClaims(
            parseResult.NameId!, parseResult.NameIdFormat, parseResult.Attributes);

        if (string.IsNullOrEmpty(userInfo.Email))
        {
            logger.LogWarning("No email address found in SAML response for connection {ConnectionId}", connectionId);
            var errorRedirect = string.IsNullOrEmpty(relayState) ? "/" : relayState;
            return Results.Redirect($"{errorRedirect}?error=saml_error&error_description={Uri.EscapeDataString("No email address found in SAML assertion.")}");
        }

        var email = userInfo.Email.ToLowerInvariant();

        // Find or create user
        var user = await userStore.FindByEmailAsync(email, ct);
        if (user is null)
        {
            // Check with provisioning service before creating
            var provisioningResult = await provisioningService.ProvisionUserAsync(new ProvisioningRequest
            {
                Email = email,
                FirstName = userInfo.FirstName,
                LastName = userInfo.LastName,
                Provider = $"saml:{connectionId}",
                ProviderKey = parseResult.NameId!
            }, ct);

            if (!provisioningResult.Approved)
            {
                logger.LogWarning("Provisioning rejected SAML user {Email}: {Reason}", email, provisioningResult.Reason);
                var errorRedirect = string.IsNullOrEmpty(relayState) ? "/" : relayState;
                return Results.Redirect($"{errorRedirect}?error=provisioning_rejected&error_description={Uri.EscapeDataString(provisioningResult.Reason ?? "Account creation not permitted.")}");
            }

            user = new AuthUser
            {
                Id = provisioningResult.UserId ?? Guid.NewGuid().ToString("N"),
                Email = email,
                NormalizedEmail = email.ToUpperInvariant(),
                EmailConfirmed = true,
                FirstName = userInfo.FirstName,
                LastName = userInfo.LastName,
                OrganizationId = provisioningResult.OrganizationId,
                CreatedAt = DateTimeOffset.UtcNow,
                LockoutEnabled = true,
                SecurityStamp = Convert.ToBase64String(
                    System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
            };

            await userStore.CreateAsync(user, ct);
            logger.LogInformation("Created new user {UserId} ({Email}) via SAML SSO", user.Id, email);
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
            new("security_stamp", user.SecurityStamp ?? "")
        };

        if (!string.IsNullOrWhiteSpace(user.OrganizationId))
            claims.Add(new Claim("org_id", user.OrganizationId));

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        await httpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);

        logger.LogInformation("User {UserId} ({Email}) signed in via SAML connection {ConnectionId}",
            user.Id, email, connectionId);

        // Redirect to RelayState
        var redirectUrl = string.IsNullOrEmpty(relayState) ? "/" : relayState;
        return Results.Redirect(redirectUrl);
    }

    private static async Task<IResult> MetadataAsync(
        string connectionId,
        ISamlProviderStore samlStore,
        IConfiguration configuration,
        CancellationToken ct)
    {
        var config = await samlStore.GetAsync(connectionId, ct);
        if (config is null)
            return Results.NotFound(new { error = "not_found", error_description = $"SAML connection '{connectionId}' not found" });

        var baseUrl = configuration["Issuer"]!;
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

    private static async Task<SamlIdpMetadata> GetCachedMetadataAsync(
        SamlProviderConfig config,
        SamlMetadataParser metadataParser,
        IMemoryCache memoryCache,
        CancellationToken ct)
    {
        var cacheKey = $"saml-metadata:{config.ConnectionId}";
        if (memoryCache.TryGetValue<SamlIdpMetadata>(cacheKey, out var cached) && cached is not null)
            return cached;

        var metadata = await metadataParser.ParseFromUrlAsync(config.MetadataLocation, ct);
        memoryCache.Set(cacheKey, metadata, MetadataCacheDuration);
        return metadata;
    }
}
