using System.Security.Cryptography;
using System.Text;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;

namespace Authagonal.Server.Endpoints.Admin;

public static class ScimTokenEndpoints
{
    public static IEndpointRouteBuilder MapScimTokenAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/scim/tokens")
            .RequireAuthorization("IdentityAdmin")
            .WithTags("Admin - SCIM Tokens");

        group.MapPost("/", GenerateTokenAsync).DisableAntiforgery();
        group.MapGet("/", ListTokensAsync);
        group.MapDelete("/{tokenId}", RevokeTokenAsync);

        return app;
    }

    private static async Task<IResult> GenerateTokenAsync(
        GenerateScimTokenRequest request,
        IScimTokenStore scimTokenStore,
        IClientStore clientStore,
        IAuditLogger audit,
        HttpContext http,
        ILogger<Program> logger,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ClientId))
            return TypedResults.Json(new ErrorInfoResponse { Error = "invalid_request", ErrorDescription = "clientId is required" }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);

        var client = await clientStore.GetAsync(request.ClientId, ct);
        if (client is null)
            return TypedResults.Json(new ErrorInfoResponse { Error = "client_not_found", ErrorDescription = $"Client '{request.ClientId}' not found" }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 404);

        // Bounded, because DateTimeOffset.AddDays throws out of range and the throw becomes a 500 with no
        // indication of which field was wrong and no audit row — the audit call is after the store write. Both
        // sibling admin endpoints that take a day count bound it; this one did not, so a value computed from a
        // UI date picker, or a plain int.MaxValue, produced an opaque server error.
        const int MaxExpiresInDays = 3650;
        if (request.ExpiresInDays is { } days && (days < 0 || days > MaxExpiresInDays))
            return TypedResults.Json(new ErrorInfoResponse
            {
                Error = "invalid_request",
                ErrorDescription = $"expiresInDays must be between 0 and {MaxExpiresInDays} (0 means no expiry).",
            }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);

        var allowedDomains = (request.AllowedEmailDomains ?? [])
            .Select(d => d?.Trim() ?? "")
            .Where(d => d.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // A domain that cannot match anything would read as a bound while permitting nothing, which is a
        // worse failure than refusing it here: the connector would 400 on every user with no way to see why.
        foreach (var domain in allowedDomains)
        {
            if (domain.Contains('@') || domain.Contains(' ') || !domain.Contains('.'))
                return TypedResults.Json(new ErrorInfoResponse
                {
                    Error = "invalid_request",
                    ErrorDescription = $"'{domain}' is not a domain — supply bare domains such as 'acme.example'",
                }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);
        }

        // Generate a cryptographically secure token
        var rawTokenBytes = RandomNumberGenerator.GetBytes(32);
        var rawToken = Convert.ToBase64String(rawTokenBytes);

        // Hash for storage
        var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken))).ToLowerInvariant();

        var token = new ScimToken
        {
            TokenId = Guid.NewGuid().ToString("N"),
            ClientId = request.ClientId,
            TokenHash = tokenHash,
            Description = request.Description,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = request.ExpiresInDays > 0
                ? DateTimeOffset.UtcNow.AddDays(request.ExpiresInDays.Value)
                : null,
            AllowedEmailDomains = allowedDomains,
        };

        await scimTokenStore.StoreAsync(token, ct);

        // A SCIM token is a directory-wide read/write credential, shown once and never recoverable. It was
        // recorded only as an unstructured log line with no actor, so the audit trail — which faithfully
        // records who renamed a client — held nothing about who minted the credential that can rewrite
        // every user in the tenant.
        await audit.LogAsync(AdminActor.Of(http), "scim_token.created", "scim_token", token.TokenId, token.ClientId, ct);

        logger.LogInformation("SCIM token generated: {TokenId} for client {ClientId}", token.TokenId, token.ClientId);

        // Said out loud, because the unrestricted case is the default and its consequence is not obvious: a
        // SCIM-created user is written EmailConfirmed = true, so an unrestricted connector can mint a
        // pre-verified account for any address in any domain — and FederationAdoptionPolicy adopts a record
        // with no external logins unconditionally, so the real owner's first federated sign-in binds to it.
        if (allowedDomains.Count == 0)
        {
            logger.LogWarning(
                "SCIM token {TokenId} for client {ClientId} is UNRESTRICTED: it may provision users in any "
                + "email domain, and a SCIM-created user is marked email-confirmed. Set allowedEmailDomains "
                + "when minting, or Scim:Clients:{ClientId}:AllowedEmailDomains in configuration.",
                token.TokenId, token.ClientId, token.ClientId);
        }

        // Return the raw token once — it cannot be recovered later
        return TypedResults.Json(new ScimTokenCreatedResponse
        {
            TokenId = token.TokenId,
            ClientId = token.ClientId,
            Token = rawToken,
            Description = token.Description,
            CreatedAt = token.CreatedAt,
            ExpiresAt = token.ExpiresAt ?? default,
            AllowedEmailDomains = token.AllowedEmailDomains,
        }, AuthagonalJsonContext.Default.ScimTokenCreatedResponse);
    }

    private static async Task<IResult> ListTokensAsync(
        string? clientId,
        IScimTokenStore scimTokenStore,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            return TypedResults.Json(new ErrorInfoResponse { Error = "invalid_request", ErrorDescription = "clientId query parameter is required" }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);

        var tokens = await scimTokenStore.GetByClientAsync(clientId, ct);

        var result = tokens.Select(t => new ScimTokenInfo
        {
            TokenId = t.TokenId,
            ClientId = t.ClientId,
            Description = t.Description,
            CreatedAt = t.CreatedAt,
            ExpiresAt = t.ExpiresAt ?? default,
            IsRevoked = t.IsRevoked,
        });

        return TypedResults.Json(new ScimTokenListResponse { Tokens = result }, AuthagonalJsonContext.Default.ScimTokenListResponse);
    }

    private static async Task<IResult> RevokeTokenAsync(
        string tokenId,
        string? clientId,
        IScimTokenStore scimTokenStore,
        IAuditLogger audit,
        HttpContext http,
        ILogger<Program> logger,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            return TypedResults.Json(new ErrorInfoResponse { Error = "invalid_request", ErrorDescription = "clientId query parameter is required" }, AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);

        await scimTokenStore.RevokeAsync(tokenId, clientId, ct);

        // Revocation is the other half: cutting a connector's access is exactly the change an operator
        // later has to prove was deliberate rather than an attacker disabling provisioning.
        await audit.LogAsync(AdminActor.Of(http), "scim_token.revoked", "scim_token", tokenId, clientId, ct);

        logger.LogInformation("SCIM token revoked: {TokenId} for client {ClientId}", tokenId, clientId);

        return TypedResults.Json(new SuccessResponse(), AuthagonalJsonContext.Default.SuccessResponse);
    }
}

public sealed class GenerateScimTokenRequest
{
    public string? ClientId { get; set; }
    public string? Description { get; set; }
    public int? ExpiresInDays { get; set; }

    /// <summary>
    /// Email domains this credential may provision into. Omit or leave empty for unrestricted.
    /// </summary>
    /// <remarks>
    /// A SCIM token is a directory-wide write credential, and this is the only thing that bounds WHICH
    /// identities it may create. It was previously reachable only from
    /// <c>Scim:Clients:{clientId}:AllowedEmailDomains</c>, which this endpoint could not write and no
    /// document mentioned — so the documented way to onboard a connector produced an unrestricted one.
    /// Intersected with that configuration key, so a token can only narrow an operator's bound.
    /// </remarks>
    public List<string>? AllowedEmailDomains { get; set; }
}
