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
        ISecretProvider secretProvider,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ConnectionName))
            return Results.BadRequest(new { error = "invalid_request", error_description = "ConnectionName is required" });

        if (string.IsNullOrWhiteSpace(request.EntityId))
            return Results.BadRequest(new { error = "invalid_request", error_description = "EntityId is required" });

        // F49: a connection is fed by a metadata URL OR pasted metadata XML (for IdPs with no
        // metadata URL, e.g. Google Workspace). Exactly one must be supplied.
        var hasUrl = !string.IsNullOrWhiteSpace(request.MetadataLocation);
        var hasXml = !string.IsNullOrWhiteSpace(request.MetadataXml);
        if (hasUrl == hasXml)
            return Results.BadRequest(new { error = "invalid_request", error_description = "Provide exactly one of MetadataLocation (a metadata URL) or MetadataXml (pasted IdP metadata)." });

        string? condensedXml = null;
        if (hasXml && CondenseMetadataXml(request.MetadataXml!, out condensedXml) is { } xmlError)
            return xmlError;

        if (ValidateNameIdFormat(request.NameIdFormat) is { } nameIdError)
            return nameIdError;

        var connectionId = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;

        var config = new SamlProviderConfig
        {
            ConnectionId = connectionId,
            ConnectionName = request.ConnectionName,
            IconUrl = request.IconUrl,
            EntityId = request.EntityId,
            MetadataLocation = request.MetadataLocation ?? "",
            MetadataXml = condensedXml,
            NameIdFormat = request.NameIdFormat,
            SignAuthnRequests = request.SignAuthnRequests,
            AllowedDomains = request.AllowedDomains ?? [],
            JitProvisioningEnabled = request.JitProvisioningEnabled,
            ChallengeMfaAfterLogin = request.ChallengeMfaAfterLogin ?? true,
            ProvisioningAttributeParams = request.ProvisioningAttributeParams ?? [],
            AllowUninvitedJit = request.AllowUninvitedJit,
            CreatedAt = now
        };

        // F54: every connection gets an SP keypair (secret-provider-protected). It enables
        // EncryptedAssertion decryption, signed AuthnRequests and signed logout messages.
        config.SpCertificate = await secretProvider.ProtectAsync(
            $"saml-{connectionId}-sp-key", Services.Saml.SamlSpKey.CreateCertificate(config.EntityId), ct);

        if (await ValidateDomainsAsync(config.AllowedDomains, connectionId, ssoDomainStore, ct) is { } domainError)
            return domainError;

        await samlStore.UpsertAsync(config, ct);
        config.SpCertificate = null; // server-only — never returned to API callers

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

        config.SpCertificate = null; // server-only — never returned to API callers
        return Results.Ok(config);
    }

    private static async Task<IResult> UpdateSamlConnection(
        string connectionId,
        UpdateSamlRequest request,
        ISamlProviderStore samlStore,
        ISsoDomainStore ssoDomainStore,
        CancellationToken ct)
    {
        var config = await samlStore.GetAsync(connectionId, ct);
        if (config is null)
            return Results.NotFound(new { error = "not_found", error_description = $"SAML connection '{connectionId}' not found" });

        // Partial update — only fields supplied on the wire are modified.
        var domainsChanged = request.AllowedDomains is not null;
        if (domainsChanged)
        {
            config.AllowedDomains = request.AllowedDomains!;
            if (await ValidateDomainsAsync(config.AllowedDomains, connectionId, ssoDomainStore, ct) is { } domainError)
                return domainError;
        }
        if (request.JitProvisioningEnabled.HasValue)
        {
            config.JitProvisioningEnabled = request.JitProvisioningEnabled.Value;
        }
        if (request.ProvisioningAttributeParams is not null)
        {
            config.ProvisioningAttributeParams = request.ProvisioningAttributeParams;
        }
        if (request.AllowUninvitedJit.HasValue)
        {
            config.AllowUninvitedJit = request.AllowUninvitedJit.Value;
        }
        if (request.ChallengeMfaAfterLogin.HasValue)
        {
            config.ChallengeMfaAfterLogin = request.ChallengeMfaAfterLogin.Value;
        }
        // F49/F51 partial updates. Supplying a metadata URL clears pasted XML and vice versa (the
        // two are mutually exclusive sources); NameIdFormat "" resets to the default.
        if (!string.IsNullOrWhiteSpace(request.MetadataLocation))
        {
            config.MetadataLocation = request.MetadataLocation;
            config.MetadataXml = null;
        }
        if (!string.IsNullOrWhiteSpace(request.MetadataXml))
        {
            if (CondenseMetadataXml(request.MetadataXml, out var condensed) is { } xmlError)
                return xmlError;
            config.MetadataXml = condensed;
            config.MetadataLocation = "";
        }
        if (request.NameIdFormat is not null)
        {
            if (ValidateNameIdFormat(request.NameIdFormat) is { } nameIdError)
                return nameIdError;
            config.NameIdFormat = string.IsNullOrWhiteSpace(request.NameIdFormat) ? null : request.NameIdFormat;
        }
        if (request.SignAuthnRequests.HasValue)
        {
            config.SignAuthnRequests = request.SignAuthnRequests.Value;
        }
        config.UpdatedAt = DateTimeOffset.UtcNow;
        await samlStore.UpsertAsync(config, ct);
        config.SpCertificate = null; // server-only — never returned to API callers

        // Re-register domain mappings only when the domain list actually changed —
        // toggling JIT shouldn't churn the SsoDomain table.
        if (domainsChanged)
        {
            await ssoDomainStore.DeleteByConnectionAsync(connectionId, ct);
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

        // InteractionPath is later concatenated onto LoginAppUrl to build a redirect; a value missing the
        // leading '/' (e.g. ".evil.com/x") would alter the host. Require a leading slash.
        if (!string.IsNullOrEmpty(request.InteractionPath) && !request.InteractionPath.StartsWith('/'))
            return Results.BadRequest(new { error = "invalid_request", error_description = "InteractionPath must start with '/'" });

        var connectionId = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;

        // Protect the client secret (stores in vault if configured, otherwise plaintext)
        var protectedSecret = await secretProvider.ProtectAsync(
            $"oidc-{connectionId}-client-secret", request.ClientSecret, ct);

        var config = new OidcProviderConfig
        {
            ConnectionId = connectionId,
            ConnectionName = request.ConnectionName,
            IconUrl = request.IconUrl,
            MetadataLocation = request.MetadataLocation,
            ClientId = request.ClientId,
            ClientSecret = protectedSecret,
            RedirectUrl = request.RedirectUrl,
            AllowedDomains = request.AllowedDomains ?? [],
            PassthroughParams = request.PassthroughParams ?? [],
            JitProvisioningEnabled = request.JitProvisioningEnabled,
            ChallengeMfaAfterLogin = request.ChallengeMfaAfterLogin ?? true,
            InteractionPath = request.InteractionPath,
            CreatedAt = now
        };

        if (await ValidateDomainsAsync(config.AllowedDomains, connectionId, ssoDomainStore, ct) is { } domainError)
            return domainError;

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

        // Return a copy with the secret stripped — never mutate the stored/returned config itself
        // (some stores hand back the cached instance, which would wipe the real secret).
        return Results.Created($"/api/v1/oidc/connections/{connectionId}", WithoutSecret(config));
    }

    private static async Task<IResult> GetOidcConnection(
        string connectionId,
        IOidcProviderStore oidcStore,
        CancellationToken ct)
    {
        var config = await oidcStore.GetAsync(connectionId, ct);
        if (config is null)
            return Results.NotFound(new { error = "not_found", error_description = $"OIDC connection '{connectionId}' not found" });

        // Copy with the secret stripped — never mutate the stored/returned instance.
        return Results.Ok(WithoutSecret(config));
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

    // Reject malformed domains and domains already mapped to a DIFFERENT connection, so one
    // connection can't hijack SSO routing for a domain another connection already owns.
    /// <summary>
    /// F49: parse-validate pasted IdP metadata and condense it to the canonical minimal form (vendor
    /// documents can exceed the 64KB Azure Table property cap; the parts the SP consumes are a few KB).
    /// Returns an error result on unparseable input, else null with the condensed XML in the out param.
    /// </summary>
    private static IResult? CondenseMetadataXml(string metadataXml, out string? condensed)
    {
        try
        {
            condensed = Authagonal.Server.Services.Saml.SamlMetadataParser.Condense(metadataXml);
            return null;
        }
        catch (Exception ex)
        {
            condensed = null;
            return Results.BadRequest(new
            {
                error = "invalid_metadata",
                error_description = $"The pasted metadata XML could not be parsed as SAML IdP metadata: {ex.Message} " +
                    "Paste the full EntityDescriptor document your IdP provides (it must contain an IDPSSODescriptor with a signing certificate and a SingleSignOnService)."
            });
        }
    }

    /// <summary>F51: NameIdFormat must be null/empty, "none", or a plausible URN.</summary>
    private static IResult? ValidateNameIdFormat(string? nameIdFormat)
    {
        if (string.IsNullOrWhiteSpace(nameIdFormat) ||
            string.Equals(nameIdFormat, Authagonal.Server.Services.Saml.SamlRequestBuilder.NameIdFormatNone, StringComparison.OrdinalIgnoreCase) ||
            nameIdFormat.StartsWith("urn:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return Results.BadRequest(new
        {
            error = "invalid_request",
            error_description = "NameIdFormat must be omitted (emailAddress default), \"none\" (omit NameIDPolicy — recommended for ADFS), or a NameID format URN."
        });
    }

    private static async Task<IResult?> ValidateDomainsAsync(
        IEnumerable<string> domains, string connectionId, ISsoDomainStore ssoDomainStore, CancellationToken ct)
    {
        foreach (var raw in domains)
        {
            var domain = raw.Trim().ToLowerInvariant();
            if (!IsValidDomain(domain))
                return Results.BadRequest(new { error = "invalid_domain", error_description = $"Invalid domain: '{raw}'" });

            var existing = await ssoDomainStore.GetAsync(domain, ct);
            if (existing is not null && !string.Equals(existing.ConnectionId, connectionId, StringComparison.Ordinal))
                return Results.BadRequest(new { error = "domain_claimed", error_description = $"Domain '{domain}' is already mapped to another SSO connection" });
        }
        return null;
    }

    private static bool IsValidDomain(string domain) =>
        domain.Length is > 0 and <= 253
        && domain.Contains('.')
        && !domain.StartsWith('.') && !domain.EndsWith('.') && !domain.Contains("..")
        && domain.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-');

    // Copy with the client secret cleared, for safe return to admins without mutating the
    // stored/cached instance.
    private static OidcProviderConfig WithoutSecret(OidcProviderConfig config) => config with { ClientSecret = "" };

    // Request DTOs

    public sealed class CreateSamlRequest
    {
        public string ConnectionName { get; set; } = "";
        /// <summary>Optional branding icon URL for the "Continue with {name}" login button.</summary>
        public string? IconUrl { get; set; }
        public string EntityId { get; set; } = "";
        /// <summary>IdP metadata URL. Exactly one of this or <see cref="MetadataXml"/> is required.</summary>
        public string? MetadataLocation { get; set; }
        /// <summary>Pasted IdP metadata XML, for IdPs with no metadata URL (Google Workspace). F49.</summary>
        public string? MetadataXml { get; set; }
        /// <summary>NameIDPolicy format: null = emailAddress default, "none" = omit (ADFS-safe), or a URN. F51.</summary>
        public string? NameIdFormat { get; set; }
        /// <summary>Force signed AuthnRequests; null = sign only when the IdP metadata requests it. F54.</summary>
        public bool? SignAuthnRequests { get; set; }
        public List<string>? AllowedDomains { get; set; }

        /// <summary>
        /// Opt this connection into JIT provisioning (auto-create unknown users on first login).
        /// Default false — an unknown assertion is rejected until explicitly enabled.
        /// </summary>
        public bool JitProvisioningEnabled { get; set; }

        /// <summary>Still route users through the local MFA challenge after federated login (F42).
        /// Default true; false = the tenant trusts the IdP's own MFA as the second factor.</summary>
        public bool? ChallengeMfaAfterLogin { get; set; }

        /// <summary>Authorize-request query params captured as provisioning CustomAttributes on a JIT
        /// user (e.g. an org invite's acceptKind/acceptToken), so an SSO signup completes an invite
        /// through the same provisioning pipeline as a password signup.</summary>
        public List<string>? ProvisioningAttributeParams { get; set; }

        /// <summary>Auto-provision an uninvited domain user on SSO login (tagged with the connection so the
        /// downstream places them in the right tenant), instead of requiring an invite.</summary>
        public bool AllowUninvitedJit { get; set; }
    }

    /// <summary>
    /// Partial update — fields left null on the wire are not modified.
    /// Replaces the legacy <c>UpdateSamlDomainsRequest</c>.
    /// </summary>
    public sealed class UpdateSamlRequest
    {
        public List<string>? AllowedDomains { get; set; }
        public bool? JitProvisioningEnabled { get; set; }
        /// <summary>Still route users through the local MFA challenge after federated login (F42);
        /// null = leave unchanged. False = the tenant trusts the IdP's own MFA.</summary>
        public bool? ChallengeMfaAfterLogin { get; set; }
        /// <summary>Authorize-request query params captured as provisioning CustomAttributes on a JIT
        /// user; null = leave unchanged.</summary>
        public List<string>? ProvisioningAttributeParams { get; set; }
        /// <summary>Auto-provision an uninvited domain user on SSO login; null = leave unchanged.</summary>
        public bool? AllowUninvitedJit { get; set; }
        /// <summary>New metadata URL; setting it clears any pasted MetadataXml.</summary>
        public string? MetadataLocation { get; set; }
        /// <summary>New pasted metadata XML; setting it clears MetadataLocation.</summary>
        public string? MetadataXml { get; set; }
        /// <summary>NameIDPolicy format; "" resets to the emailAddress default, "none" omits. F51.</summary>
        public string? NameIdFormat { get; set; }
        /// <summary>Force signed AuthnRequests; null = leave unchanged. F54.</summary>
        public bool? SignAuthnRequests { get; set; }
    }

    public sealed class CreateOidcRequest
    {
        public string ConnectionName { get; set; } = "";
        /// <summary>Optional branding icon URL for the "Continue with {name}" login button.</summary>
        public string? IconUrl { get; set; }
        public string MetadataLocation { get; set; } = "";
        public string ClientId { get; set; } = "";
        public string ClientSecret { get; set; } = "";
        public string RedirectUrl { get; set; } = "";
        public List<string>? AllowedDomains { get; set; }
        public List<string>? PassthroughParams { get; set; }
        /// <summary>Opt this connection into JIT provisioning (auto-create unknown federated users on
        /// first login). Default false — an unknown assertion is rejected until explicitly enabled.</summary>
        public bool JitProvisioningEnabled { get; set; }

        /// <summary>Still route users through the local MFA challenge after federated login (F42).
        /// Default true; false = the tenant trusts the IdP's own MFA as the second factor.</summary>
        public bool? ChallengeMfaAfterLogin { get; set; }

        /// <summary>Optional login-app path rendered before federating an unauthenticated idp_hint
        /// request through this connection (see OidcProviderConfig.InteractionPath).</summary>
        public string? InteractionPath { get; set; }
    }
}
