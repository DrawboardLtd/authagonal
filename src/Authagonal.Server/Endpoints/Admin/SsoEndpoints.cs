using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;

namespace Authagonal.Server.Endpoints.Admin;

public static class SsoEndpoints
{
    public static IEndpointRouteBuilder MapSsoAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var samlGroup = app.MapGroup("/api/v1/saml/connections")
            .RequireAuthorization("IdentityAdmin")
            .WithTags("Admin - SAML");

        samlGroup.MapPost("/", CreateSamlConnection);
        samlGroup.MapGet("/{connectionId}", GetSamlConnection);
        samlGroup.MapPut("/{connectionId}", UpdateSamlConnection);
        samlGroup.MapDelete("/{connectionId}", DeleteSamlConnection);

        var oidcGroup = app.MapGroup("/api/v1/oidc/connections")
            .RequireAuthorization("IdentityAdmin")
            .WithTags("Admin - OIDC");

        oidcGroup.MapPost("/", CreateOidcConnection);
        oidcGroup.MapGet("/{connectionId}", GetOidcConnection);
        oidcGroup.MapDelete("/{connectionId}", DeleteOidcConnection);

        app.MapGet("/api/v1/sso/domains", GetAllSsoDomains)
            .RequireAuthorization("IdentityAdmin")
            .WithTags("Admin - SSO");

        return app;
    }

    // SAML endpoints

    private static async Task<IResult> CreateSamlConnection(
        CreateSamlRequest request,
        ISamlProviderStore samlStore,
        ISsoDomainStore ssoDomainStore,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ConnectionName))
            return Results.BadRequest(new { error = "invalid_request", error_description = "ConnectionName is required" });

        if (string.IsNullOrWhiteSpace(request.EntityId))
            return Results.BadRequest(new { error = "invalid_request", error_description = "EntityId is required" });

        if (string.IsNullOrWhiteSpace(request.MetadataLocation))
            return Results.BadRequest(new { error = "invalid_request", error_description = "MetadataLocation is required" });

        var connectionId = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;

        var config = new SamlProviderConfig
        {
            ConnectionId = connectionId,
            ConnectionName = request.ConnectionName,
            EntityId = request.EntityId,
            MetadataLocation = request.MetadataLocation,
            AllowedDomains = request.AllowedDomains ?? [],
            CreatedAt = now
        };

        await samlStore.UpsertAsync(config, ct);

        // Register SSO domains
        foreach (var domain in config.AllowedDomains)
        {
            await ssoDomainStore.UpsertAsync(new SsoDomain
            {
                Domain = domain.ToLowerInvariant(),
                ProviderType = "saml",
                ConnectionId = connectionId,
                Scheme = $"saml-{connectionId}"
            }, ct);
        }

        return Results.Created($"/api/v1/saml/connections/{connectionId}", config);
    }

    private static async Task<IResult> GetSamlConnection(
        string connectionId,
        ISamlProviderStore samlStore,
        CancellationToken ct)
    {
        var config = await samlStore.GetAsync(connectionId, ct);
        if (config is null)
            return Results.NotFound(new { error = "not_found", error_description = $"SAML connection '{connectionId}' not found" });

        return Results.Ok(config);
    }

    private static async Task<IResult> UpdateSamlConnection(
        string connectionId,
        UpdateSamlDomainsRequest request,
        ISamlProviderStore samlStore,
        ISsoDomainStore ssoDomainStore,
        CancellationToken ct)
    {
        var config = await samlStore.GetAsync(connectionId, ct);
        if (config is null)
            return Results.NotFound(new { error = "not_found", error_description = $"SAML connection '{connectionId}' not found" });

        // Remove old domain mappings
        await ssoDomainStore.DeleteByConnectionAsync(connectionId, ct);

        // Update domains
        config.AllowedDomains = request.AllowedDomains ?? [];
        config.UpdatedAt = DateTimeOffset.UtcNow;
        await samlStore.UpsertAsync(config, ct);

        // Register new domain mappings
        foreach (var domain in config.AllowedDomains)
        {
            await ssoDomainStore.UpsertAsync(new SsoDomain
            {
                Domain = domain.ToLowerInvariant(),
                ProviderType = "saml",
                ConnectionId = connectionId,
                Scheme = $"saml-{connectionId}"
            }, ct);
        }

        return Results.Ok(config);
    }

    private static async Task<IResult> DeleteSamlConnection(
        string connectionId,
        ISamlProviderStore samlStore,
        ISsoDomainStore ssoDomainStore,
        CancellationToken ct)
    {
        var config = await samlStore.GetAsync(connectionId, ct);
        if (config is null)
            return Results.NotFound(new { error = "not_found", error_description = $"SAML connection '{connectionId}' not found" });

        await ssoDomainStore.DeleteByConnectionAsync(connectionId, ct);
        await samlStore.DeleteAsync(connectionId, ct);

        return Results.NoContent();
    }

    // OIDC endpoints

    private static async Task<IResult> CreateOidcConnection(
        CreateOidcRequest request,
        IOidcProviderStore oidcStore,
        ISsoDomainStore ssoDomainStore,
        ISecretProvider secretProvider,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ConnectionName))
            return Results.BadRequest(new { error = "invalid_request", error_description = "ConnectionName is required" });

        if (string.IsNullOrWhiteSpace(request.MetadataLocation))
            return Results.BadRequest(new { error = "invalid_request", error_description = "MetadataLocation is required" });

        if (string.IsNullOrWhiteSpace(request.ClientId))
            return Results.BadRequest(new { error = "invalid_request", error_description = "ClientId is required" });

        if (string.IsNullOrWhiteSpace(request.ClientSecret))
            return Results.BadRequest(new { error = "invalid_request", error_description = "ClientSecret is required" });

        if (string.IsNullOrWhiteSpace(request.RedirectUrl))
            return Results.BadRequest(new { error = "invalid_request", error_description = "RedirectUrl is required" });

        var connectionId = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;

        // Protect the client secret (stores in vault if configured, otherwise plaintext)
        var protectedSecret = await secretProvider.ProtectAsync(
            $"oidc-{connectionId}-client-secret", request.ClientSecret, ct);

        var config = new OidcProviderConfig
        {
            ConnectionId = connectionId,
            ConnectionName = request.ConnectionName,
            MetadataLocation = request.MetadataLocation,
            ClientId = request.ClientId,
            ClientSecret = protectedSecret,
            RedirectUrl = request.RedirectUrl,
            AllowedDomains = request.AllowedDomains ?? [],
            CreatedAt = now
        };

        await oidcStore.UpsertAsync(config, ct);

        foreach (var domain in config.AllowedDomains)
        {
            await ssoDomainStore.UpsertAsync(new SsoDomain
            {
                Domain = domain.ToLowerInvariant(),
                ProviderType = "oidc",
                ConnectionId = connectionId,
                Scheme = $"oidc-{connectionId}"
            }, ct);
        }

        return Results.Created($"/api/v1/oidc/connections/{connectionId}", config);
    }

    private static async Task<IResult> GetOidcConnection(
        string connectionId,
        IOidcProviderStore oidcStore,
        CancellationToken ct)
    {
        var config = await oidcStore.GetAsync(connectionId, ct);
        if (config is null)
            return Results.NotFound(new { error = "not_found", error_description = $"OIDC connection '{connectionId}' not found" });

        return Results.Ok(config);
    }

    private static async Task<IResult> DeleteOidcConnection(
        string connectionId,
        IOidcProviderStore oidcStore,
        ISsoDomainStore ssoDomainStore,
        CancellationToken ct)
    {
        var config = await oidcStore.GetAsync(connectionId, ct);
        if (config is null)
            return Results.NotFound(new { error = "not_found", error_description = $"OIDC connection '{connectionId}' not found" });

        await ssoDomainStore.DeleteByConnectionAsync(connectionId, ct);
        await oidcStore.DeleteAsync(connectionId, ct);

        return Results.NoContent();
    }

    // SSO domains

    private static async Task<IResult> GetAllSsoDomains(
        ISsoDomainStore ssoDomainStore,
        CancellationToken ct)
    {
        var domains = await ssoDomainStore.GetAllAsync(ct);
        return Results.Ok(domains);
    }

    // Request DTOs

    public sealed record CreateSamlRequest(
        string ConnectionName,
        string EntityId,
        string MetadataLocation,
        List<string>? AllowedDomains);

    public sealed record UpdateSamlDomainsRequest(List<string>? AllowedDomains);

    public sealed record CreateOidcRequest(
        string ConnectionName,
        string MetadataLocation,
        string ClientId,
        string ClientSecret,
        string RedirectUrl,
        List<string>? AllowedDomains);
}
