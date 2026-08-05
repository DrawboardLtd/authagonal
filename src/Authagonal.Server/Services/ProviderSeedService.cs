using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.Server.Endpoints;

namespace Authagonal.Server.Services;

public sealed class ProviderSeedService(
    ISamlProviderStore samlStore,
    IOidcProviderStore oidcStore,
    ISsoDomainStore ssoDomainStore,
    ISecretProvider secretProvider,
    IConfiguration configuration,
    ILogger<ProviderSeedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        await SeedSamlProviders(ct);
        await SeedOidcProviders(ct);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    private async Task SeedSamlProviders(CancellationToken ct)
    {
        var providers = configuration.GetSection("SamlProviders").Get<List<SamlProviderSeed>>();
        if (providers is null || providers.Count == 0)
            return;

        foreach (var seed in providers)
        {
            if (string.IsNullOrWhiteSpace(seed.ConnectionId))
            {
                logger.LogWarning("Skipping SAML provider seed entry with missing ConnectionId");
                continue;
            }

            // Read, then merge — do NOT rebuild from the seed.
            //
            // This constructed a brand-new SamlProviderConfig and upserted it, and SamlProviderSeed has no
            // field for SpCertificate, SignAuthnRequests, NameIdFormat, MetadataXml or IconUrl. So every one
            // of those was written back as NULL on every pod start for any connection named in the config
            // section — destroying the SP keypair (breaking EncryptedAssertion decryption, signed
            // AuthnRequests and signed logout, all of which resolve that secret by name), reverting
            // admin-set AuthnRequest signing to unsigned, and resetting CreatedAt to now.
            //
            // Same defect class the two client seeders already record as fixed: "every property the seed does
            // not state reverted to the MODEL DEFAULT on each pod start — silently undoing admin hardening
            // applied through PUT". This was the third seeder, and it had not been converted.
            var existing = await samlStore.GetAsync(seed.ConnectionId, ct);

            var config = existing ?? new SamlProviderConfig
            {
                ConnectionId = seed.ConnectionId,
                EntityId = "",
                MetadataLocation = "",
                CreatedAt = DateTimeOffset.UtcNow,
            };

            config.ConnectionName = seed.ConnectionName ?? existing?.ConnectionName ?? seed.ConnectionId;
            config.EntityId = seed.EntityId ?? (existing?.EntityId is { Length: > 0 } e ? e
                : throw new InvalidOperationException(
                    $"SAML provider '{seed.ConnectionId}' is missing required EntityId"));
            config.MetadataLocation = seed.MetadataLocation ?? (existing?.MetadataLocation is { Length: > 0 } m ? m
                : throw new InvalidOperationException(
                    $"SAML provider '{seed.ConnectionId}' is missing required MetadataLocation"));
            config.AllowedDomains = seed.AllowedDomains ?? existing?.AllowedDomains ?? [];
            config.JitProvisioningEnabled = seed.JitProvisioningEnabled;
            config.ChallengeMfaAfterLogin = seed.ChallengeMfaAfterLogin;
            config.ProvisioningAttributeParams =
                seed.ProvisioningAttributeParams ?? existing?.ProvisioningAttributeParams ?? [];
            config.AllowUninvitedJit = seed.AllowUninvitedJit;
            config.AllowUnsolicitedResponses = seed.AllowUnsolicitedResponses;
            if (existing is not null) config.UpdatedAt = DateTimeOffset.UtcNow;

            await samlStore.UpsertAsync(config, ct);

            foreach (var domain in config.AllowedDomains)
            {
                await ssoDomainStore.UpsertAsync(new SsoDomain
                {
                    Domain = domain.ToLowerInvariant(),
                    ProviderType = "saml",
                    ConnectionId = config.ConnectionId,
                    Scheme = $"saml-{config.ConnectionId}"
                }, ct);
            }

            logger.LogInformation("Seeded SAML provider {Id} ({Name})", config.ConnectionId, config.ConnectionName);
        }
    }

    private async Task SeedOidcProviders(CancellationToken ct)
    {
        var providers = configuration.GetSection("OidcProviders").Get<List<OidcProviderSeed>>();
        if (providers is null || providers.Count == 0)
            return;

        foreach (var seed in providers)
        {
            if (string.IsNullOrWhiteSpace(seed.ConnectionId))
            {
                logger.LogWarning("Skipping OIDC provider seed entry with missing ConnectionId");
                continue;
            }

            var protectedSecret = await secretProvider.ProtectAsync(
                $"oidc-{seed.ConnectionId}-client-secret",
                seed.ClientSecret ?? throw new InvalidOperationException(
                    $"OIDC provider '{seed.ConnectionId}' is missing required ClientSecret"),
                ct);

            // Same read-merge-write as the SAML half above. The OIDC seed states nearly every field, so the
            // blast radius is smaller — IconUrl was overwritten with null and CreatedAt reset to now on every
            // pod start — but it is the same shape, and the next field added to the model without a matching
            // seed field would be silently reverted on boot.
            var existingOidc = await oidcStore.GetAsync(seed.ConnectionId, ct);

            // Said once per seeded connection that names one, because a value here reads as configuration
            // and is not: the redirect_uri is derived per request from the issuer, so this is the field an
            // operator most plausibly gets wrong and never finds out about.
            var derivedCallback = (configuration["Issuer"] ?? "").TrimEnd('/') + OidcEndpoints.CallbackPath;
            if (!string.IsNullOrWhiteSpace(seed.RedirectUrl)
                && !string.Equals(seed.RedirectUrl, derivedCallback, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning(
                    "OIDC provider seed '{ConnectionId}' sets RedirectUrl '{Supplied}', which is ignored: the "
                    + "redirect_uri is derived per request as {Derived}. Register that URI with the upstream "
                    + "provider; the seed value has no effect.",
                    seed.ConnectionId, seed.RedirectUrl, derivedCallback);
            }

            var config = new OidcProviderConfig
            {
                ConnectionId = seed.ConnectionId,
                IconUrl = existingOidc?.IconUrl,
                ConnectionName = seed.ConnectionName ?? existingOidc?.ConnectionName ?? seed.ConnectionId,
                MetadataLocation = seed.MetadataLocation ?? throw new InvalidOperationException(
                    $"OIDC provider '{seed.ConnectionId}' is missing required MetadataLocation"),
                ClientId = seed.ClientId ?? throw new InvalidOperationException(
                    $"OIDC provider '{seed.ConnectionId}' is missing required ClientId"),
                ClientSecret = protectedSecret,
                // Not required, and it used to throw at startup for a value nothing reads. The callback is
                // derived per request from the issuer (OidcEndpoints.CallbackUriFor) — a stored value could
                // not be right for every tenant sharing a connection. Kept as supplied so an existing row
                // round-trips unchanged; a supplied value that is not the derived one is warned about below.
                RedirectUrl = seed.RedirectUrl ?? "",
                AllowedDomains = seed.AllowedDomains ?? [],
                JitProvisioningEnabled = seed.JitProvisioningEnabled,
                UseUpstreamSubjectAsUserId = seed.UseUpstreamSubjectAsUserId,
                ShowOnLogin = seed.ShowOnLogin,
                ChallengeMfaAfterLogin = seed.ChallengeMfaAfterLogin,
                AutoLinkExistingByEmail = seed.AutoLinkExistingByEmail,
                PassthroughParams = seed.PassthroughParams ?? [],
                ProvisioningAttributeParams = seed.ProvisioningAttributeParams ?? [],
                RevalidateOnRefresh = seed.RevalidateOnRefresh,
                AllowUninvitedJit = seed.AllowUninvitedJit,
                IsExternalConnection = seed.IsExternalConnection,
                SessionExpClaim = seed.SessionExpClaim,
                InteractionPath = seed.InteractionPath,
                CreatedAt = existingOidc?.CreatedAt ?? DateTimeOffset.UtcNow,
                UpdatedAt = existingOidc is null ? null : DateTimeOffset.UtcNow,
            };

            await oidcStore.UpsertAsync(config, ct);

            foreach (var domain in config.AllowedDomains)
            {
                await ssoDomainStore.UpsertAsync(new SsoDomain
                {
                    Domain = domain.ToLowerInvariant(),
                    ProviderType = "oidc",
                    ConnectionId = config.ConnectionId,
                    Scheme = $"oidc-{config.ConnectionId}"
                }, ct);
            }

            logger.LogInformation("Seeded OIDC provider {Id} ({Name})", config.ConnectionId, config.ConnectionName);
        }
    }

    public sealed class SamlProviderSeed
    {
        public string? ConnectionId { get; set; }
        public string? ConnectionName { get; set; }
        public string? EntityId { get; set; }
        public string? MetadataLocation { get; set; }
        public List<string>? AllowedDomains { get; set; }
        public bool JitProvisioningEnabled { get; set; }
        public bool ChallengeMfaAfterLogin { get; set; } = true;
        public List<string>? ProvisioningAttributeParams { get; set; }
        public bool AllowUninvitedJit { get; set; }

        /// <summary>Accept IdP-initiated (unsolicited) responses on this connection. Default false.</summary>
        public bool AllowUnsolicitedResponses { get; set; }
    }

    public sealed class OidcProviderSeed
    {
        public string? ConnectionId { get; set; }
        public string? ConnectionName { get; set; }
        public string? MetadataLocation { get; set; }
        public string? ClientId { get; set; }
        public string? ClientSecret { get; set; }
        public string? RedirectUrl { get; set; }
        public List<string>? AllowedDomains { get; set; }
        public bool JitProvisioningEnabled { get; set; }
        public bool UseUpstreamSubjectAsUserId { get; set; }
        public bool ShowOnLogin { get; set; } = true;
        public bool ChallengeMfaAfterLogin { get; set; } = true;
        public bool AutoLinkExistingByEmail { get; set; }
        public List<string>? PassthroughParams { get; set; }
        public List<string>? ProvisioningAttributeParams { get; set; }
        public bool RevalidateOnRefresh { get; set; }
        public bool AllowUninvitedJit { get; set; }
        public bool IsExternalConnection { get; set; }
        public string? SessionExpClaim { get; set; }
        public string? InteractionPath { get; set; }
    }
}
