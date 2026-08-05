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

    /// <summary>
    /// What an SSO write is recorded as. A connection decides which external IdP may assert who a user is,
    /// and with JIT enabled it can create accounts — so creating or repointing one is the strongest
    /// account-takeover lever in the product, and it left no audit record at all. The domains travel in the
    /// detail because "which domain did they claim" is the fact that turns a row into an incident.
    /// </summary>
    private static string DomainDetail(string connectionName, IEnumerable<string> domains)
    {
        var list = string.Join(", ", domains);
        return list.Length == 0 ? connectionName : $"{connectionName} [{list}]";
    }

    private static async Task<IResult> CreateSamlConnection(
        CreateSamlRequest request,
        ISamlProviderStore samlStore,
        ISsoDomainStore ssoDomainStore,
        ISecretProvider secretProvider,
        IAuditLogger audit,
        HttpContext http,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ConnectionName))
            return Results.BadRequest(new { error = "invalid_request", error_description = "ConnectionName is required" });

        if (string.IsNullOrWhiteSpace(request.EntityId))
            return Results.BadRequest(new { error = "invalid_request", error_description = "EntityId is required" });

        if (ValidateEntityId(request.EntityId) is { } entityIdError)
            return entityIdError;

        // F49: a connection is fed by a metadata URL OR pasted metadata XML (for IdPs with no
        // metadata URL, e.g. Google Workspace). Exactly one must be supplied.
        var hasUrl = !string.IsNullOrWhiteSpace(request.MetadataLocation);
        var hasXml = !string.IsNullOrWhiteSpace(request.MetadataXml);
        if (hasUrl == hasXml)
            return Results.BadRequest(new { error = "invalid_request", error_description = "Provide exactly one of MetadataLocation (a metadata URL) or MetadataXml (pasted IdP metadata)." });

        if (hasUrl && ValidateMetadataLocation(request.MetadataLocation!, http) is { } urlError)
            return urlError;

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
            AllowUnsolicitedResponses = request.AllowUnsolicitedResponses,
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

        await audit.LogAsync(AdminActor.Of(http), "saml_connection.created", "saml_connection", connectionId,
            DomainDetail(config.ConnectionName, config.AllowedDomains), ct);

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
        IAuditLogger audit,
        HttpContext http,
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
        if (request.AllowUnsolicitedResponses.HasValue)
        {
            config.AllowUnsolicitedResponses = request.AllowUnsolicitedResponses.Value;
        }
        // F49/F51 partial updates. Supplying a metadata URL clears pasted XML and vice versa (the
        // two are mutually exclusive sources); NameIdFormat "" resets to the default.
        if (!string.IsNullOrWhiteSpace(request.MetadataLocation))
        {
            if (ValidateMetadataLocation(request.MetadataLocation, http) is { } urlError)
                return urlError;
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

        // Repointing an existing connection — new metadata, or a new domain list — is the same takeover
        // lever as creating one, and cheaper for an attacker because the connection is already trusted.
        await audit.LogAsync(AdminActor.Of(http), "saml_connection.updated", "saml_connection", connectionId,
            DomainDetail(config.ConnectionName, config.AllowedDomains), ct);

        return Results.Ok(config);
    }

    private static async Task<IResult> DeleteSamlConnection(
        string connectionId,
        ISamlProviderStore samlStore,
        ISsoDomainStore ssoDomainStore,
        IAuditLogger audit,
        HttpContext http,
        CancellationToken ct)
    {
        var config = await samlStore.GetAsync(connectionId, ct);
        if (config is null)
            return Results.NotFound(new { error = "not_found", error_description = $"SAML connection '{connectionId}' not found" });

        await ssoDomainStore.DeleteByConnectionAsync(connectionId, ct);
        await samlStore.DeleteAsync(connectionId, ct);

        // Deleting a connection locks every user who signs in through it out of their tenant, so it is a
        // denial-of-service an operator must be able to attribute.
        await audit.LogAsync(AdminActor.Of(http), "saml_connection.deleted", "saml_connection", connectionId,
            DomainDetail(config.ConnectionName, config.AllowedDomains), ct);

        return Results.NoContent();
    }

    // OIDC endpoints

    private static async Task<IResult> CreateOidcConnection(
        CreateOidcRequest request,
        IOidcProviderStore oidcStore,
        ISsoDomainStore ssoDomainStore,
        ISecretProvider secretProvider,
        IAuditLogger audit,
        ITenantContext tenantContext,
        ILogger<CreateOidcRequest> logger,
        HttpContext http,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ConnectionName))
            return Results.BadRequest(new { error = "invalid_request", error_description = "ConnectionName is required" });

        if (string.IsNullOrWhiteSpace(request.MetadataLocation))
            return Results.BadRequest(new { error = "invalid_request", error_description = "MetadataLocation is required" });

        // The same validation the SAML twin applies on create AND update, and it was missing here — an
        // absent-sibling, not an absent rule. `IsNullOrWhiteSpace` was the only check, so any string was
        // stored: this document supplies `issuer` and `jwks_uri` for the connection, and OidcEndpoints
        // takes ValidIssuer and IssuerSigningKeys straight from it, so over cleartext an on-path party
        // substitutes both together and every upstream id_token then validates against attacker keys. The
        // runtime funnel in OidcDiscoveryClient does now require https and bind the metadata URL to the
        // issuer, so this is defence in depth rather than the only barrier — but storing an unvalidated
        // location means the refusal surfaces as a failed login later instead of as a message to the admin
        // who caused it. The SSRF guard applies for the same reason it applies to provisioning callbacks:
        // this is a server-fetched URL.
        //
        // Create is the only verb that needed it: this group exposes MapPost/MapGet/MapDelete, with no
        // update route for an OIDC connection.
        if (ValidateMetadataLocation(request.MetadataLocation!, http) is { } oidcMetadataError)
            return oidcMetadataError;

        if (string.IsNullOrWhiteSpace(request.ClientId))
            return Results.BadRequest(new { error = "invalid_request", error_description = "ClientId is required" });

        if (string.IsNullOrWhiteSpace(request.ClientSecret))
            return Results.BadRequest(new { error = "invalid_request", error_description = "ClientSecret is required" });

        // RedirectUrl is NOT required, and used to be — a 400 demanding a value that nothing reads. The
        // callback is derived per request from the issuer (OidcEndpoints.CallbackUriFor), because in a
        // multi-tenant host it has to be on the origin the browser is on. An administrator who supplied a
        // different value got no error and no effect; now they get a line saying so.
        var derivedCallback = OidcEndpoints.CallbackUriFor(tenantContext.Issuer);
        if (!string.IsNullOrWhiteSpace(request.RedirectUrl)
            && !string.Equals(request.RedirectUrl, derivedCallback, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning(
                "OIDC connection '{ConnectionName}' was created with RedirectUrl '{Supplied}', which is "
                + "ignored: the redirect_uri is derived per request as {Derived}. Register that URI with the "
                + "upstream provider.",
                request.ConnectionName, request.RedirectUrl, derivedCallback);
        }

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

        await audit.LogAsync(AdminActor.Of(http), "oidc_connection.created", "oidc_connection", connectionId,
            DomainDetail(config.ConnectionName, config.AllowedDomains), ct);

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
        IAuditLogger audit,
        HttpContext http,
        CancellationToken ct)
    {
        var config = await oidcStore.GetAsync(connectionId, ct);
        if (config is null)
            return Results.NotFound(new { error = "not_found", error_description = $"OIDC connection '{connectionId}' not found" });

        await ssoDomainStore.DeleteByConnectionAsync(connectionId, ct);
        await oidcStore.DeleteAsync(connectionId, ct);

        await audit.LogAsync(AdminActor.Of(http), "oidc_connection.deleted", "oidc_connection", connectionId,
            DomainDetail(config.ConnectionName, config.AllowedDomains), ct);

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

    /// <summary>
    /// A SAML metadata URL must be an absolute https URL that passes the SSRF guard.
    /// </summary>
    /// <remarks>
    /// SamlMetadataParser refuses anything else at fetch time — this document carries the signing
    /// certificates every assertion is checked against, so over cleartext any on-path party substitutes
    /// its own and mints assertions this SP accepts. Refusing it here too is what makes that refusal
    /// visible: stored unvalidated, an <c>http://</c> location is accepted by the admin API, looks
    /// configured in the portal, and surfaces only as a failed login later — by which point the operator
    /// is debugging SSO rather than reading a message about TLS. The SSRF guard is applied for the same
    /// reason it is applied to provisioning callbacks: a metadata URL is a server-fetched URL.
    /// </remarks>
    /// <param name="http">
    /// Only for the operator's internal-destination allowlist. Resolved from the request container rather
    /// than taken as a handler parameter: an unregistered service in a minimal-API signature is inferred as
    /// a BODY parameter, and a host that composes its own container and never registers one would see every
    /// create/update 400 with nothing explaining why. Absent means the strict list, which is the posture
    /// this check has always had.
    /// </param>
    private static IResult? ValidateMetadataLocation(string metadataLocation, HttpContext http)
    {
        var allowlist = http.RequestServices.GetService<Authagonal.Core.Services.OutboundAllowlist>();

        // The allowlist is consulted for the same reason the fetch consults it: federating with an IdP
        // reachable only over a private network is a first-class deployment, and refusing the URL here
        // while SamlMetadataParser would have fetched it happily would be the two halves of one guard
        // disagreeing. https is still required with no exception — this document carries the signing
        // certificates every assertion is checked against, and a private network is not a secure channel.
        if (Uri.TryCreate(metadataLocation, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && Authagonal.Core.Services.OutboundUrl.IsSafe(metadataLocation, allowlist: allowlist))
        {
            return null;
        }

        return Results.BadRequest(new
        {
            error = "invalid_request",
            error_description = "MetadataLocation must be an absolute https URL on a publicly routable host. " +
                "The metadata document carries the signing certificates every assertion is validated against, " +
                "so it cannot be fetched over plaintext http. Paste the document into MetadataXml instead if " +
                "the IdP publishes no https metadata endpoint. An IdP on your own internal network can be " +
                "reached by listing it in Auth:AllowedInternalTargets."
        });
    }

    /// <summary>
    /// The SP entityID must be what SAML says it is — an absolute URI — and no longer than the 1024
    /// characters Metadata §2.2.1 caps it at.
    /// </summary>
    /// <remarks>
    /// This value is written by a connection admin, who in a multi-tenant deployment is a lower
    /// privilege than the platform, and it is then interpolated into two documents other parties
    /// consume as authoritative: the outbound AuthnRequest/LogoutRequest this SP signs with its own
    /// key, and the anonymous <c>/saml/{id}/metadata</c> response. Both interpolations escape, so this
    /// is defence in depth rather than the only barrier — but free text reaching a signed protocol
    /// message is worth refusing at the point it is stored, where the error is attributable to the
    /// admin who caused it instead of surfacing later as an unexplained IdP rejection.
    /// </remarks>
    private static IResult? ValidateEntityId(string entityId)
    {
        // Uri.TryCreate alone is not enough: it accepts a space or a double quote and silently
        // percent-encodes them, so `https://sp.test/x" WantAssertionsSigned="false` parses as a valid
        // absolute URI. Those are exactly the characters that break out of an XML attribute, and RFC
        // 3986 excludes every one of them from a URI anyway. Non-ASCII is left alone — an IRI-form
        // entityID is legal and carries none of these.
        var wellFormed = entityId.Length <= 1024
            && Uri.TryCreate(entityId, UriKind.Absolute, out _)
            && !entityId.Any(c => c <= ' ' || c is '"' or '<' or '>' or '\\' or '^' or '`' or '{' or '|' or '}' or (char)0x7F);

        if (wellFormed)
            return null;

        return Results.BadRequest(new
        {
            error = "invalid_request",
            error_description = "EntityId must be an absolute URI (https://… or a urn:…) of at most 1024 characters, with no spaces or URI-illegal characters."
        });
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

        /// <summary>
        /// Accept IdP-initiated (unsolicited) responses — ones carrying no <c>InResponseTo</c>. Default
        /// false: such a response is bound to no pending request and no browser, so anyone with an account
        /// at the IdP could otherwise establish a session here from any user-agent.
        /// </summary>
        public bool AllowUnsolicitedResponses { get; set; }
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

        /// <summary>Accept IdP-initiated (unsolicited) responses; null = leave unchanged.</summary>
        public bool? AllowUnsolicitedResponses { get; set; }
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
