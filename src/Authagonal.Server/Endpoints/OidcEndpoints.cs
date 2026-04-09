using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.Server.Services.Oidc;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Authagonal.Server.Endpoints;

public static class OidcEndpoints
{
    public static IEndpointRouteBuilder MapOidcEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/oidc/{connectionId}/login", LoginAsync).AllowAnonymous();
        app.MapGet("/oidc/callback", CallbackAsync).AllowAnonymous();

        return app;
    }

    private static async Task<IResult> LoginAsync(
        string connectionId,
        string? returnUrl,
        IOidcProviderStore oidcStore,
        OidcDiscoveryClient discoveryClient,
        OidcStateStore stateStore,
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

        // Build authorization URL
        var baseUrl = tenantContext.Issuer;
        var redirectUri = $"{baseUrl}/oidc/callback";

        var authorizationUrl = $"{discovery.AuthorizationEndpoint}" +
            $"?client_id={Uri.EscapeDataString(config.ClientId)}" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            $"&response_type=code" +
            $"&scope={Uri.EscapeDataString("openid profile email")}" +
            $"&state={Uri.EscapeDataString(state)}" +
            $"&nonce={Uri.EscapeDataString(nonce)}" +
            $"&code_challenge={Uri.EscapeDataString(codeChallenge)}" +
            $"&code_challenge_method=S256";

        logger.LogInformation("OIDC login initiated for connection {ConnectionId}, returnUrl={ReturnUrl}", connectionId, effectiveReturnUrl);

        return Results.Redirect(authorizationUrl);
    }

    private static async Task<IResult> CallbackAsync(
        HttpContext httpContext,
        IOidcProviderStore oidcStore,
        IUserStore userStore,
        IEnumerable<IAuthHook> authHooks,
        OidcDiscoveryClient discoveryClient,
        OidcStateStore stateStore,
        IHttpClientFactory httpClientFactory,
        ISecretProvider secretProvider,
        Authagonal.Core.Services.ITenantContext tenantContext,
        IProvisioningOrchestrator provisioning,
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
            return Results.Redirect($"{returnUrl}?error=oidc_error&error_description={Uri.EscapeDataString("OIDC connection not found")}");
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
            return Results.Redirect($"{returnUrl}?error=oidc_error&error_description={Uri.EscapeDataString("Failed to fetch provider configuration")}");
        }

        // Exchange code for tokens
        var baseUrl = tenantContext.Issuer;
        var redirectUri = $"{baseUrl}/oidc/callback";

        // Resolve the client secret (may be a Key Vault reference)
        var clientSecret = await secretProvider.ResolveAsync(config.ClientSecret, ct);

        string idToken;
        string? accessToken;
        try
        {
            (idToken, accessToken) = await ExchangeCodeForTokensAsync(
                httpClientFactory, discovery.TokenEndpoint, code, redirectUri,
                config.ClientId, clientSecret, stateData.CodeVerifier, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "OIDC token exchange failed for connection {ConnectionId}", stateData.ConnectionId);
            return Results.Redirect($"{returnUrl}?error=oidc_error&error_description={Uri.EscapeDataString("Token exchange failed")}");
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
                ClockSkew = TimeSpan.FromMinutes(5)
            };

            validationResult = await tokenHandler.ValidateTokenAsync(idToken, validationParameters);

            if (!validationResult.IsValid)
            {
                logger.LogWarning("OIDC id_token validation failed: {Error}", validationResult.Exception?.Message);
                return Results.Redirect($"{returnUrl}?error=oidc_error&error_description={Uri.EscapeDataString("ID token validation failed")}");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "OIDC id_token validation threw for connection {ConnectionId}", stateData.ConnectionId);
            return Results.Redirect($"{returnUrl}?error=oidc_error&error_description={Uri.EscapeDataString("ID token validation failed")}");
        }

        // Verify nonce — must be present and match the stored value
        var nonceClaim = Claim(validationResult.Claims, "nonce");
        if (string.IsNullOrEmpty(nonceClaim) ||
            !string.Equals(nonceClaim, stateData.Nonce, StringComparison.Ordinal))
        {
            logger.LogWarning("OIDC nonce missing or mismatch for connection {ConnectionId}", stateData.ConnectionId);
            return Results.Redirect($"{returnUrl}?error=oidc_error&error_description={Uri.EscapeDataString("Nonce validation failed")}");
        }

        // Extract claims from validated id_token
        var sub = Claim(validationResult.Claims, "sub");
        var email = ExtractEmail(validationResult.Claims);
        var name = Claim(validationResult.Claims, "name");
        var givenName = Claim(validationResult.Claims, "given_name");
        var familyName = Claim(validationResult.Claims, "family_name");

        // If no email in id_token, try userinfo endpoint
        if (string.IsNullOrEmpty(email) && !string.IsNullOrEmpty(accessToken) && !string.IsNullOrEmpty(discovery.UserinfoEndpoint))
        {
            try
            {
                var userinfoClaims = await FetchUserinfoAsync(httpClientFactory, discovery.UserinfoEndpoint, accessToken, ct);
                email ??= ExtractEmailFromJson(userinfoClaims);
                name ??= userinfoClaims.GetValueOrDefault("name") as string;
                givenName ??= userinfoClaims.GetValueOrDefault("given_name") as string;
                familyName ??= userinfoClaims.GetValueOrDefault("family_name") as string;

            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to fetch userinfo for connection {ConnectionId}", stateData.ConnectionId);
            }
        }

        if (string.IsNullOrEmpty(email))
        {
            logger.LogWarning("No email found in OIDC claims for connection {ConnectionId}", stateData.ConnectionId);
            return Results.Redirect($"{returnUrl}?error=oidc_error&error_description={Uri.EscapeDataString("No email address found in identity token")}");
        }

        if (string.IsNullOrEmpty(sub))
        {
            logger.LogWarning("No sub claim found in OIDC id_token for connection {ConnectionId}", stateData.ConnectionId);
            return Results.Redirect($"{returnUrl}?error=oidc_error&error_description={Uri.EscapeDataString("No subject identifier found in identity token")}");
        }

        email = email.ToLowerInvariant();

        // Derive first/last name from "name" if given_name/family_name are not present
        if (string.IsNullOrEmpty(givenName) && !string.IsNullOrEmpty(name))
        {
            var parts = name.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            givenName = parts.Length > 0 ? parts[0] : null;
            familyName ??= parts.Length > 1 ? parts[1] : null;
        }

        // Find or create user (same pattern as SamlEndpoints ACS)
        var user = await userStore.FindByEmailAsync(email, ct);
        if (user is null)
        {
            if (config.DisableJitProvisioning)
            {
                logger.LogInformation("JIT provisioning disabled for OIDC connection {ConnectionId}, rejecting unknown user {Email}", stateData.ConnectionId, email);
                return Results.Redirect($"{returnUrl}?error=access_denied&error_description={Uri.EscapeDataString("User not found. Contact your administrator to be provisioned.")}");
            }

            user = new AuthUser
            {
                Id = Guid.NewGuid().ToString("N"),
                Email = email,
                NormalizedEmail = email.ToUpperInvariant(),
                EmailConfirmed = true,
                FirstName = givenName,
                LastName = familyName,
                CreatedAt = DateTimeOffset.UtcNow,
                LockoutEnabled = true,
                SecurityStamp = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            };

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
            return Results.Redirect($"{returnUrl}?error=account_disabled&error_description={Uri.EscapeDataString("Account has been deactivated.")}");
        }

        // Ensure external login link
        var provider = $"oidc:{stateData.ConnectionId}";
        var providerKey = sub;
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

        logger.LogInformation("User {UserId} ({Email}) signed in via OIDC connection {ConnectionId}",
            user.Id, email, stateData.ConnectionId);

        await authHooks.RunOnUserAuthenticatedAsync(user.Id, email, "oidc", ct: ct);

        return Results.Redirect(returnUrl);
    }

    private static async Task<(string IdToken, string? AccessToken)> ExchangeCodeForTokensAsync(
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

        return (idToken, accessToken);
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

    private static string SanitizeReturnUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "/login";

        // Only allow relative paths to prevent open redirect
        if (!url.StartsWith('/') || url.StartsWith("//"))
            return "/login";

        return url;
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
