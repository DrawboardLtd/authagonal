using System.Security.Cryptography;
using System.Text.Json;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.Server.Services;

namespace Authagonal.Server.Endpoints;

public static class DeviceAuthorizationEndpoint
{
    public static IEndpointRouteBuilder MapDeviceAuthorizationEndpoints(this IEndpointRouteBuilder app)
    {
        // RFC 8628 §3.1 — Device Authorization Request
        app.MapPost("/connect/deviceauthorization", async (
            HttpContext httpContext,
            IClientStore clientStore,
            IGrantStore grantStore,
            ITenantContext tenantContext,
            PasswordHasher passwordHasher,
            IConfiguration configuration,
            CancellationToken ct) =>
        {
            var form = await httpContext.Request.ReadFormAsync(ct);
            var clientId = form["client_id"].FirstOrDefault();
            var clientSecret = form["client_secret"].FirstOrDefault();
            var scope = form["scope"].FirstOrDefault() ?? "openid";

            if (string.IsNullOrWhiteSpace(clientId))
                return DeviceError("invalid_client", "client_id is required");

            var client = await clientStore.GetAsync(clientId, ct);
            if (client is null)
                return DeviceError("invalid_client", "Unknown client");

            if (!client.AllowedGrantTypes.Contains("urn:ietf:params:oauth:grant-type:device_code", StringComparer.OrdinalIgnoreCase))
                return DeviceError("unauthorized_client", "Device authorization grant not allowed for this client");

            // Verify secret if required
            if (client.RequireClientSecret)
            {
                if (string.IsNullOrWhiteSpace(clientSecret))
                    return DeviceError("invalid_client", "client_secret is required");

                var valid = client.ClientSecretHashes.Any(hash =>
                {
                    var result = passwordHasher.VerifyPassword(clientSecret, hash);
                    return result is PasswordVerifyResult.Success or PasswordVerifyResult.SuccessRehashNeeded;
                });

                if (!valid)
                    return DeviceError("invalid_client", "Invalid client credentials");
            }

            // Generate codes
            var deviceCode = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var userCode = GenerateUserCode();
            // Zero means the client was persisted before this field existed — use the 5-minute
            // default that Duende also ships with so behaviour stays predictable across imports.
            var expiresIn = client.DeviceCodeLifetimeSeconds > 0 ? client.DeviceCodeLifetimeSeconds : 300;

            var requestedScopes = scope.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var invalidScopes = requestedScopes.Except(client.AllowedScopes, StringComparer.OrdinalIgnoreCase).ToArray();
            if (invalidScopes.Length > 0)
                return DeviceError("invalid_scope", $"Scopes not allowed: {string.Join(", ", invalidScopes)}");
            var validScopes = requestedScopes.ToList();

            // Store as a persisted grant
            var data = JsonSerializer.Serialize(new DeviceCodeData
            {
                UserCode = userCode,
                ClientId = clientId,
                Scopes = validScopes,
                IsApproved = false,
                SubjectId = null,
            }, AuthagonalJsonContext.Default.DeviceCodeData);

            await grantStore.StoreAsync(new PersistedGrant
            {
                Key = $"device:{deviceCode}",
                Type = "device_code",
                ClientId = clientId,
                Data = data,
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn),
            }, ct);

            // Also index by user_code for the approval page
            await grantStore.StoreAsync(new PersistedGrant
            {
                Key = $"device_user:{userCode}",
                Type = "device_user_code",
                ClientId = clientId,
                Data = deviceCode, // points back to the device code
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn),
            }, ct);

            var verificationUri = $"{tenantContext.Issuer}/device";

            return TypedResults.Json(new DeviceAuthorizationResponse
            {
                DeviceCode = deviceCode,
                UserCode = userCode,
                VerificationUri = verificationUri,
                VerificationUriComplete = $"{verificationUri}?user_code={userCode}",
                ExpiresIn = expiresIn,
            }, AuthagonalJsonContext.Default.DeviceAuthorizationResponse);
        })
        .AllowAnonymous()
        .DisableAntiforgery()
        .WithTags("OAuth");

        // User approval endpoint — called by the login app after authentication
        app.MapPost("/api/auth/device/approve", async (
            HttpContext httpContext,
            IGrantStore grantStore,
            CancellationToken ct) =>
        {
            if (httpContext.User.Identity?.IsAuthenticated != true)
                return JsonResults.Error("not_authenticated", 401);

            var form = await httpContext.Request.ReadFormAsync(ct);
            var userCode = form["user_code"].FirstOrDefault()?.Trim().ToUpperInvariant();

            if (string.IsNullOrWhiteSpace(userCode))
                return TypedResults.Json(new ErrorInfoResponse { Error = "user_code_required" }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);

            // Look up the user code
            var userCodeGrant = await grantStore.GetAsync($"device_user:{userCode}", ct);
            if (userCodeGrant is null || userCodeGrant.ConsumedAt is not null || userCodeGrant.ExpiresAt < DateTimeOffset.UtcNow)
                return TypedResults.Json(new ErrorInfoResponse { Error = "invalid_user_code", Message = "Code is invalid or expired" }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);

            var deviceCode = userCodeGrant.Data;
            var deviceGrant = await grantStore.GetAsync($"device:{deviceCode}", ct);
            if (deviceGrant is null || deviceGrant.ExpiresAt < DateTimeOffset.UtcNow)
                return TypedResults.Json(new ErrorInfoResponse { Error = "expired", Message = "Device code has expired" }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);

            // Approve — write the subject ID into the device code data
            var subjectId = httpContext.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                ?? httpContext.User.FindFirst("sub")?.Value;

            if (string.IsNullOrEmpty(subjectId))
                return JsonResults.Error("missing_identity", 401);

            var data = JsonSerializer.Deserialize(deviceGrant.Data, AuthagonalJsonContext.Default.DeviceCodeData)!;
            data.IsApproved = true;
            data.SubjectId = subjectId;

            deviceGrant.Data = JsonSerializer.Serialize(data, AuthagonalJsonContext.Default.DeviceCodeData);
            deviceGrant.SubjectId = subjectId;
            await grantStore.StoreAsync(deviceGrant, ct);

            // Consume the user code so it can't be reused
            await grantStore.ConsumeAsync($"device_user:{userCode}", ct);

            return TypedResults.Json(new DeviceApprovedResponse(), AuthagonalJsonContext.Default.DeviceApprovedResponse);
        })
        .DisableAntiforgery()
        .WithTags("OAuth");

        return app;
    }

    private static string GenerateUserCode()
    {
        // 8-character alphanumeric (no ambiguous chars: 0/O, 1/I/L)
        const string chars = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
        var bytes = RandomNumberGenerator.GetBytes(8);
        var code = new char[8];
        for (var i = 0; i < 8; i++)
            code[i] = chars[bytes[i] % chars.Length];
        return $"{new string(code, 0, 4)}-{new string(code, 4, 4)}";
    }

    private static IResult DeviceError(string error, string description) =>
        JsonResults.OAuthError(error, description,
            statusCode: error == "invalid_client" ? 401 : 400);
}

internal sealed class DeviceCodeData
{
    public required string UserCode { get; set; }
    public required string ClientId { get; set; }
    public required List<string> Scopes { get; set; }
    public bool IsApproved { get; set; }
    public string? SubjectId { get; set; }
}
