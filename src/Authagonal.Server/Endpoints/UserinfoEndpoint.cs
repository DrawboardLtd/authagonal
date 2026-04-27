using Authagonal.Core.Models;
using Authagonal.Core.Stores;
using Authagonal.Server.Services;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Authagonal.Server.Endpoints;

public static class UserinfoEndpoint
{
    public static IEndpointRouteBuilder MapUserinfoEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/connect/userinfo", async (
            HttpContext httpContext,
            Authagonal.Core.Services.IKeyManager keyManager,
            IUserStore userStore,
            IScimGroupStore scimGroupStore,
            Authagonal.Core.Services.ITenantContext tenantContext,
            CancellationToken ct) =>
        {
            // Extract Bearer token
            var authHeader = httpContext.Request.Headers.Authorization.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                return Results.Unauthorized();

            var token = authHeader["Bearer ".Length..].Trim();
            if (string.IsNullOrWhiteSpace(token))
                return Results.Unauthorized();

            // Validate the JWT
            var issuer = tenantContext.Issuer;
            var keys = keyManager.GetSecurityKeys().Select(Authagonal.Protocol.Services.ProtocolSigningKeyOps.JwkToSecurityKey).ToList();

            var validationParams = new TokenValidationParameters
            {
                ValidIssuer = issuer,
                ValidateIssuer = true,
                ValidateAudience = true,
                AudienceValidator = (audiences, _, _) => audiences?.Any() == true,
                ValidateLifetime = true,
                IssuerSigningKeys = keys,
                ValidateIssuerSigningKey = true,
                ClockSkew = TimeSpan.FromSeconds(60)
            };

            var handler = new JsonWebTokenHandler();
            var result = await handler.ValidateTokenAsync(token, validationParams);

            if (!result.IsValid)
                return Results.Unauthorized();

            var subjectId = result.Claims.TryGetValue("sub", out var sub) ? sub?.ToString() : null;
            if (string.IsNullOrWhiteSpace(subjectId))
                return Results.Unauthorized();

            var user = await userStore.GetAsync(subjectId, ct);
            if (user is null)
                return Results.Unauthorized();

            var claims = new Dictionary<string, object?>
            {
                ["sub"] = user.Id,
                ["email"] = user.Email,
                ["email_verified"] = user.EmailConfirmed
            };

            if (!string.IsNullOrWhiteSpace(user.FirstName))
                claims["given_name"] = user.FirstName;

            if (!string.IsNullOrWhiteSpace(user.LastName))
                claims["family_name"] = user.LastName;

            var fullName = $"{user.FirstName} {user.LastName}".Trim();
            if (!string.IsNullOrWhiteSpace(fullName))
                claims["name"] = fullName;

            if (!string.IsNullOrWhiteSpace(user.Phone))
                claims["phone_number"] = user.Phone;

            if (!string.IsNullOrWhiteSpace(user.OrganizationId))
                claims["org_id"] = user.OrganizationId;

            if (user.Roles.Count > 0)
                claims["roles"] = user.Roles;

            var groups = await scimGroupStore.GetGroupsByUserIdAsync(user.Id, ct);
            if (groups.Count > 0)
            {
                claims["groups"] = groups.Select(g => new { id = g.Id, name = g.DisplayName }).ToArray();
            }

            return Results.Ok(claims);
        })
        .AllowAnonymous()
        .WithTags("OAuth");

        return app;
    }
}
