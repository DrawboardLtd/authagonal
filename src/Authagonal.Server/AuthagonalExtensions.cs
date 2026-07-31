using Microsoft.AspNetCore.Http;
using System.Net.Http;
using Authagonal.Core.Clustering;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.Protocol;
using Authagonal.Protocol.Endpoints;
using Authagonal.Protocol.Services;
using Authagonal.Server.Endpoints;
using Authagonal.Server.Endpoints.Admin;
using Authagonal.Server.Endpoints.Scim;
using Authagonal.Server.Middleware;
using Authagonal.Server.Services;
using Authagonal.Server.Services.Cluster;
using Authagonal.Server.Services.Oidc;
using Authagonal.Server.Services.Saml;
using Authagonal.AzureProvider;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using System.Security.Claims;

namespace Authagonal.Server;

/// <summary>
/// Extension methods for composing Authagonal into any ASP.NET Core application.
/// This is the primary integration point for hosting Authagonal as a library.
/// </summary>
public static class AuthagonalExtensions
{
    /// <summary>
    /// Registers all Authagonal services: storage, authentication, authorization, CORS,
    /// rate limiting, health checks, and background tasks.
    /// <para>
    /// Override extensibility points by registering your implementations <b>before</b> calling this method:
    /// <c>IEmailService</c>, <c>IAuthHook</c>, <c>IProvisioningOrchestrator</c>, <c>ISecretProvider</c>.
    /// </para>
    /// </summary>
    /// <summary>
    /// Full single-tenant registration. Calls <see cref="AddAuthagonalCore"/> and adds
    /// singleton stores, KeyManager, background services, and other single-tenant infrastructure.
    /// </summary>
    public static IServiceCollection AddAuthagonal(this IServiceCollection services, IConfiguration configuration, Action<ClusteringBuilder>? configureClustering = null)
    {
        services.AddAuthagonalCore(configuration, configureClustering);

        // ---------------------------------------------------------------------------
        // Single-tenant storage. Two configuration paths:
        //   - Storage:ConnectionString — connection string with AccountKey (dev / Azurite)
        //   - Storage:TableServiceUri  — managed-identity URI like
        //     https://{account}.table.core.windows.net/. The host is responsible for
        //     granting the workload's identity Storage Table Data Contributor.
        // The MI path is preferred in production; access keys never need to land in
        // a K8s secret.
        // ---------------------------------------------------------------------------
        var storageConnectionString = configuration["Storage:ConnectionString"];
        var tableServiceUri = configuration["Storage:TableServiceUri"];
        // Default true to keep existing config-driven deployments working unchanged.
        // Set false on hosts that don't expose admin name-prefix search to skip
        // the UserFirstNames / UserLastNames index writes (which use a single hot
        // partition and cap throughput at ~2k ops/sec at scale).
        var nameIndexesEnabled = configuration.GetValue("Storage:NameIndexesEnabled", true);
        if (!services.Any(d => d.ServiceType == typeof(Authagonal.Core.Stores.IUserStore)))
        {
            if (!string.IsNullOrWhiteSpace(tableServiceUri))
            {
                services.AddTableStorage(new Uri(tableServiceUri), new Azure.Identity.DefaultAzureCredential(), nameIndexesEnabled);
            }
            else if (!string.IsNullOrWhiteSpace(storageConnectionString))
            {
                services.AddTableStorage(storageConnectionString, nameIndexesEnabled);
            }
            else
            {
                throw new InvalidOperationException("Either Storage:ConnectionString or Storage:TableServiceUri must be configured");
            }
        }

        // Data protection
        var dataProtection = services.AddDataProtection()
            .SetApplicationName("Authagonal");
        var dpBlobUri = configuration["DataProtection:BlobUri"];
        if (!string.IsNullOrWhiteSpace(dpBlobUri))
        {
            dataProtection.PersistKeysToAzureBlobStorage(new Uri(dpBlobUri), new Azure.Identity.DefaultAzureCredential());
        }
        else if (!string.IsNullOrWhiteSpace(storageConnectionString) &&
                 !storageConnectionString.Contains("devstoreaccount1", StringComparison.OrdinalIgnoreCase))
        {
            var blobServiceClient = new BlobServiceClient(storageConnectionString);
            var container = blobServiceClient.GetBlobContainerClient("dataprotection");
            container.CreateIfNotExists();
            var blobClient = container.GetBlobClient("keys.xml");
            dataProtection.PersistKeysToAzureBlobStorage(blobClient);
        }

        // Encrypt the key ring at rest when the operator supplies a key. Without this the ring is
        // persisted as PLAINTEXT XML on every backend — no IXmlEncryptor is configured by default —
        // and that ring protects the auth cookie, so anyone who can read the store can mint a valid
        // session for any user. ASP.NET Core does warn about it, at Information level, which the
        // shipped log configuration (Microsoft.AspNetCore: Warning) discards.
        var dpKeyVaultKeyId = configuration["DataProtection:KeyVaultKeyId"];
        var dpCertificateThumbprint = configuration["DataProtection:CertificateThumbprint"];
        if (!string.IsNullOrWhiteSpace(dpKeyVaultKeyId))
        {
            dataProtection.ProtectKeysWithAzureKeyVault(
                new Uri(dpKeyVaultKeyId), new Azure.Identity.DefaultAzureCredential());
        }
        else if (!string.IsNullOrWhiteSpace(dpCertificateThumbprint))
        {
            dataProtection.ProtectKeysWithCertificate(dpCertificateThumbprint);
        }

        // Neither "is the ring durable" nor "is it encrypted" is decided here. A SQL or S3 host
        // attaches its own repository — and could attach its own encryptor — before or after this
        // call, which this code cannot see in either ordering. Both questions are therefore answered
        // once, at startup, from the resolved options. That is also what fixes the encryption check
        // having been a registration-time throw guarded on the two Azure settings: correct for Azure,
        // and silent for every other persistent backend.
        services.AddSingleton<IHostedService>(sp => new KeyRingStartupCheck(
            sp.GetRequiredService<IOptions<Microsoft.AspNetCore.DataProtection.KeyManagement.KeyManagementOptions>>(),
            sp.GetRequiredService<IHostEnvironment>(),
            sp.GetRequiredService<ILogger<KeyRingStartupCheck>>(),
            configuration.GetValue("DataProtection:AllowUnencryptedKeyRing", false)));

        // Signing-key management is provided by Authagonal.Protocol's ProtocolKeyManager
        // (registered via AddAuthagonalCore → AddAuthagonalProtocol). Single-tenant hosts
        // get it as an IKeyManager singleton; multi-tenant hosts that registered their own
        // IKeyManager before AddAuthagonalCore keep theirs.

        // JWT key resolver (uses root provider, fine for singleton ProtocolKeyManager)
        services.AddSingleton<IPostConfigureOptions<JwtBearerOptions>>(sp =>
            new JwtBearerKeyResolverPostConfigure(sp));

        // Background services that depend on keyed TableClient or singleton stores
        services.AddHostedService<TokenCleanupService>();
        // Grant reconciliation scans the raw grant index tables directly (keyed TableClients), so it's
        // Azure-only; register it only when the Azure backend provided those clients. On other backends,
        // expiry cleanup is handled by TokenCleanupService via IGrantStore.RemoveExpiredAsync.
        if (services.Any(d => d.IsKeyedService && (d.ServiceKey as string) == "Grants"))
            services.AddHostedService<GrantReconciliationService>();
        services.AddHostedService<SigningKeyRotationService>();
        services.AddHostedService<ClientSeedService>();
        services.AddHostedService<ProviderSeedService>();
        services.AddHostedService<ScopeSeedService>();
        // After the scope seeder, so a scope gated on a role (Scope.AllowedRoles) and the role it
        // names come up in that order on a fresh environment.
        services.AddHostedService<RoleSeedService>();

        // SAML replay cache + OIDC state store live behind the ISamlReplayCache / IOidcStateStore seams.
        // The Azure backend exposes keyed TableClients; when present, wire the Table-backed impls. An AWS
        // host pre-registers the DynamoDB impls (AddDynamoStorage), so these gated TryAdds are no-ops there.
        if (services.Any(d => d.IsKeyedService && (d.ServiceKey as string) == "SamlReplayCache"))
            services.TryAddSingleton<ISamlReplayCache>(sp =>
                new SamlReplayCache(sp.GetRequiredKeyedService<TableClient>("SamlReplayCache"), sp.GetRequiredService<IOptions<CacheOptions>>()));
        if (services.Any(d => d.IsKeyedService && (d.ServiceKey as string) == "OidcStateStore"))
        {
            services.TryAddSingleton<IOidcStateStore>(sp =>
                new OidcStateStore(sp.GetRequiredKeyedService<TableClient>("OidcStateStore"), sp.GetRequiredService<IOptions<CacheOptions>>()));

            // Azure Table has no TTL, and the anonymous federation-login endpoint writes one of these
            // rows on every hit. Without a sweep every abandoned attempt left a permanent row holding
            // a code verifier and a nonce.
            services.AddSingleton<IHostedService, Authagonal.Server.Services.Oidc.OidcStateSweepService>();
        }

        // Health check (depends on ISigningKeyStore singleton)
        services.AddHealthChecks()
            .AddCheck<TableStorageHealthCheck>("table_storage");

        return services;
    }

    /// <summary>
    /// Registers core Authagonal services that are safe for both single-tenant and multi-tenant hosts.
    /// Does NOT register stores, KeyManager, background services, or anything that depends on
    /// singleton store resolution. Multi-tenant hosts call this and register their own equivalents.
    /// </summary>
    public static IServiceCollection AddAuthagonalCore(this IServiceCollection services, IConfiguration configuration, Action<ClusteringBuilder>? configureClustering = null)
    {
        // ---------------------------------------------------------------------------
        // Localization
        // ---------------------------------------------------------------------------
        services.AddLocalization();
        services.AddHttpContextAccessor();

        // ---------------------------------------------------------------------------
        // Tenant context — default single-tenant reads from IConfiguration.
        // Multi-tenant hosts (e.g. Cloud) register their own scoped ITenantContext
        // before calling AddAuthagonal; TryAdd ensures it is not overwritten.
        // ---------------------------------------------------------------------------
        services.TryAddSingleton<Authagonal.Core.Services.ITenantContext>(
            sp => new DefaultTenantContext(sp.GetRequiredService<IConfiguration>()));

        // ---------------------------------------------------------------------------
        // Password policy
        // ---------------------------------------------------------------------------
        var passwordPolicy = new PasswordPolicy();
        configuration.GetSection("PasswordPolicy").Bind(passwordPolicy);
        services.AddSingleton(passwordPolicy);

        // ---------------------------------------------------------------------------
        // Auth / Cache / BackgroundService options
        // ---------------------------------------------------------------------------
        services.Configure<AuthOptions>(configuration.GetSection("Auth"));

        // Refused rather than clamped: a deployment configured below the floor should fail to start
        // instead of quietly writing weaker password and client-secret hashes than its operator
        // believes it is writing, and clamping would hide the typo that caused it.
        services.AddOptions<AuthOptions>()
            .Validate(
                o => o.Pbkdf2Iterations >= AuthOptions.MinimumPbkdf2Iterations,
                $"Auth:Pbkdf2Iterations must be at least {AuthOptions.MinimumPbkdf2Iterations}. " +
                "Raising it is safe: each hash records the cost it was derived at, so existing hashes " +
                "keep verifying and are re-written on their owner's next successful login.")
            .ValidateOnStart();
        services.Configure<CacheOptions>(configuration.GetSection("Cache"));
        services.Configure<BackgroundServiceOptions>(configuration.GetSection("BackgroundServices"));
        // Cloudflare Turnstile — opt-in: verification on /login and /register is enforced
        // only when Turnstile:SecretKey is configured; otherwise the flow is unchanged.
        services.Configure<TurnstileOptions>(configuration.GetSection("Turnstile"));
        services.AddHttpClient<TurnstileVerifier>();

        // ---------------------------------------------------------------------------
        // Application services
        // ---------------------------------------------------------------------------
        services.AddSingleton<PasswordHasher>();
        services.AddSingleton<PasswordValidator>();
        services.AddHttpClient("Provisioning")
            // No automatic redirect following, and a bounded timeout. The SSRF guard only ever inspected the
            // URL the caller supplied, so an automatic 302 reached a host it never saw — see
            // SafeOutboundHttp, which resolves hops manually and re-validates each one. The timeout matches
            // every other outbound client in the codebase; these two were left at the 100-second default,
            // which made a slow remote host a request-slot amplifier on anonymous endpoints.
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(10))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });

        // Protocol — token service, key manager (when not pre-registered), auth-code
        // service. Server maps AuthOptions into AuthagonalProtocolOptions so there's one
        // source of truth for key lifetime / rotation / grace window.
        services.AddSingleton<IConfigureOptions<AuthagonalProtocolOptions>>(sp =>
        {
            var auth = sp.GetRequiredService<IOptions<AuthOptions>>().Value;
            return new ConfigureNamedOptions<AuthagonalProtocolOptions>(Options.DefaultName, o =>
            {
                o.SigningKeyLifetimeDays = auth.SigningKeyLifetimeDays;
                o.SigningKeyCacheRefreshMinutes = auth.SigningKeyCacheRefreshMinutes;
                o.RefreshTokenReuseGraceSeconds = auth.RefreshTokenReuseGraceSeconds;
                o.AuthenticationScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            });
        });

        // Server-specific client-secret verifier — uses PasswordHasher so legacy
        // PBKDF2v1, ASP.NET Identity V3, and BCrypt client-secret hashes all verify
        // through the same path as user passwords. Must TryAdd before AddAuthagonalProtocol.
        services.TryAddSingleton<IClientSecretVerifier, PasswordHasherClientSecretVerifier>();

        // Names any client still on a bare unsalted digest. The verifier upgrades the hash on first
        // successful authentication, so this only ever reports clients that are not being used —
        // which are precisely the ones the automatic upgrade cannot reach.
        services.AddSingleton<IHostedService, LegacySecretHashWarning>();

        // The XML-crypto CVE fixes come from the shared framework, not from a package this library can
        // pin — so the floor is asserted rather than assumed.
        services.AddSingleton<IHostedService, RuntimeVersionFloor>();

        // The private signing key is the one secret whose exposure is total, and its at-rest
        // protection is opt-in through IFieldCipher — so its absence is stated rather than assumed.
        services.AddSingleton<IHostedService, PlaintextSigningKeyWarning>();

        // Rate limiting is in-process per node; the authoritative global limit is enforced at the edge.
        // The concrete limiter, plus a decorator that scopes every key to the current tenant. Without the
        // decorator all tenants share one budget per logical key, so one tenant can exhaust another's.
        //
        // Registered BEFORE AddAuthagonalProtocol, like IClientSecretVerifier above and for the same
        // reason: the protocol package now TryAdds a plain InProcessRateLimiter so its own anonymous
        // write endpoints (PAR) are throttled in a host that brings nothing, and a TryAdd that ran
        // first would shadow this tenant-scoping decorator — turning a per-tenant budget back into a
        // shared one without anything failing.
        services.TryAddSingleton<InProcessRateLimiter>();
        services.TryAddSingleton<IRateLimiter>(sp => new TenantScopedRateLimiter(
            sp.GetRequiredService<InProcessRateLimiter>(),
            sp.GetRequiredService<IHttpContextAccessor>()));

        // Auth:AllowInsecureHttp has to reach the protocol options too, not just the pipeline gate.
        // MapAuthagonalEndpoints maps Protocol's PAR endpoint directly, and that endpoint carries its own
        // RFC 6749 §3.1/§3.2 filter (Authagonal.Protocol cannot rely on middleware it does not own). Two
        // switches for one decision would mean a plaintext dev host answering everything except /connect/par
        // — so the server's switch is the one an operator sets, and it is propagated here.
        var allowInsecureHttp = configuration.GetValue("Auth:AllowInsecureHttp", false);
        services.AddAuthagonalProtocol(o => o.AllowInsecureHttp = allowInsecureHttp);

        // Subject resolver — maps ClaimsPrincipal / OidcSubject back to AuthUser via the user store.
        services.AddScoped<UserStoreOidcSubjectResolver>();
        services.AddScoped<IOidcSubjectResolver>(sp => sp.GetRequiredService<UserStoreOidcSubjectResolver>());
        services.AddSingleton<TotpService>();
        services.AddSingleton<RecoveryCodeService>();
        // WebAuthn (FIDO2): the relying-party config (rp id + origin) is resolved PER REQUEST from the
        // request host inside WebAuthnService — a single startup value can't be right on a multi-tenant
        // server where each tenant is on its own host. So there's no AddFido2 singleton here.
        services.AddScoped<WebAuthnService>();

        // Extensibility points — TryAdd so custom registrations take precedence
        // Email: the built-in Resend sender activates when Email:ResendApiKey is configured.
        // Without any IEmailService, mail is discarded — and because RequireConfirmedEmailForLogin
        // defaults to true, self-registered users could never log in (UseAuthagonal warns at startup).
        if (!string.IsNullOrWhiteSpace(configuration["Email:ResendApiKey"]))
            services.TryAddSingleton<IEmailService, EmailService>();
        else
            services.TryAddSingleton<IEmailService, NullEmailService>();
        services.TryAddSingleton<IAuditLogger, NullAuditLogger>();
        services.TryAddSingleton<IClientScopeGuard, AllowAllClientScopeGuard>();
        // Capability tickets: durable, atomically single-use handles over the grant store —
        // the generalized ws-ticket primitive for hosts embedding the broker.
        services.TryAddSingleton<Authagonal.Core.Authority.ICapabilityTicketService,
            Authagonal.Core.Authority.GrantStoreCapabilityTicketService>();
        services.TryAddSingleton<IProvisioningAppQuota, UnlimitedProvisioningAppQuota>();
        // SCIM group → role mappings (empty default; the cloud registers a per-tenant store).
        services.TryAddSingleton<IScimGroupRoleMappingStore, InMemoryScimGroupRoleMappingStore>();
        services.TryAddScoped<IProvisioningAppProvider, ConfigProvisioningAppProvider>();
        services.TryAddScoped<IProvisioningOrchestrator, TccProvisioningOrchestrator>();
        // Auth hooks — multiple IAuthHook implementations can be registered and all will run.
        // NullAuthHook is only added if no hooks are registered by the host.
        if (!services.Any(s => s.ServiceType == typeof(IAuthHook)))
            services.AddSingleton<IAuthHook, NullAuthHook>();

        // Secret provider: defaults to plaintext; set SecretProvider:VaultUri to use Key Vault.
        // UseAuthagonal warns at startup when the plaintext default is what a non-Development host
        // ends up with, because "documented in one line of installation.md" is not a diagnostic.
        var secretProviderOptions = new SecretProviderOptions();
        configuration.GetSection("SecretProvider").Bind(secretProviderOptions);
        services.TryAddSingleton(secretProviderOptions);

        var vaultUri = configuration["SecretProvider:VaultUri"];
        if (!string.IsNullOrWhiteSpace(vaultUri))
        {
            var secretClient = new Azure.Security.KeyVault.Secrets.SecretClient(
                new Uri(vaultUri), new Azure.Identity.DefaultAzureCredential());
            services.AddSingleton(secretClient);
            services.AddSingleton<ISecretProvider, KeyVaultSecretProvider>();
        }
        else
        {
            services.TryAddSingleton<ISecretProvider, PlaintextSecretProvider>();
        }

        // ---------------------------------------------------------------------------
        // SAML services
        // ---------------------------------------------------------------------------
        services.AddHttpClient("SamlMetadata")
            // No automatic redirect following, and a bounded timeout. The SSRF guard only ever inspected the
            // URL the caller supplied, so an automatic 302 reached a host it never saw — see
            // SafeOutboundHttp, which resolves hops manually and re-validates each one. The timeout matches
            // every other outbound client in the codebase; these two were left at the 100-second default,
            // which made a slow remote host a request-slot amplifier on anonymous endpoints.
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(10))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.AddMemoryCache();
        services.AddSingleton<SamlMetadataParser>();
        services.AddSingleton<SamlResponseParser>();
        // ---------------------------------------------------------------------------
        // OIDC services
        // ---------------------------------------------------------------------------
        services.AddHttpClient("OidcDiscovery")
            // No automatic redirect following, and a bounded timeout. The SSRF guard only ever inspected the
            // URL the caller supplied, so an automatic 302 reached a host it never saw — see
            // SafeOutboundHttp, which resolves hops manually and re-validates each one. The timeout matches
            // every other outbound client in the codebase; these two were left at the 100-second default,
            // which made a slow remote host a request-slot amplifier on anonymous endpoints.
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(10))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.AddSingleton<OidcDiscoveryClient>();

        // ---------------------------------------------------------------------------
        // Authentication
        // ---------------------------------------------------------------------------
        var cookieLifetimeHours = configuration.GetValue("Authentication:CookieLifetimeHours", 48);

        // Server-side session storage (opt-in seam). If the host registers an ITicketStore, the cookie
        // auth ticket is persisted server-side and the cookie carries only a session key — giving instant
        // per-session revocation and the ability to enumerate a user's active sessions. Registered as a
        // PostConfigure so it runs after AddCookie's own Configure and can resolve the (optional) store
        // from DI. With no store the self-contained cookie below is used, unchanged (the security-stamp
        // revalidation in OnValidatePrincipal still applies in both modes).
        services.AddOptions<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme)
            .PostConfigure<IServiceProvider>((options, sp) =>
            {
                if (sp.GetService<ITicketStore>() is { } ticketStore)
                    options.SessionStore = ticketStore;
            });

        services.AddAuthentication(options =>
        {
            options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        })
        .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
        {
            options.LoginPath = "/login";
            options.ExpireTimeSpan = TimeSpan.FromHours(cookieLifetimeHours);
            options.SlidingExpiration = true;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            // Secure by default.
            //
            // SameAsRequest looks equivalent behind a TLS-terminating proxy, but it depends on
            // X-Forwarded-Proto arriving and being trusted: a misconfigured ingress, a health probe on
            // plain HTTP, or a proxy that drops the header yields a NON-Secure session cookie, which
            // then rides any plaintext request to the same host. The failure is silent and the cookie
            // is the whole session. Hosts that genuinely serve over HTTP — local development — set
            // Authentication:AllowInsecureCookie.
            var allowInsecureCookie = configuration.GetValue("Authentication:AllowInsecureCookie", false);
            var cookieDomain = configuration["Authentication:CookieDomain"];

            options.Cookie.SecurePolicy = allowInsecureCookie
                ? CookieSecurePolicy.SameAsRequest
                : CookieSecurePolicy.Always;

            // Authentication:CookieDomain was previously read only to suppress the __Host- prefix and
            // was never applied, so setting it cost the operator origin binding and bought them the
            // domain-scoped cookie they had asked for — strictly worse than leaving it unset.
            if (!string.IsNullOrEmpty(cookieDomain))
                options.Cookie.Domain = cookieDomain;

            // __Host- binds the cookie to this exact origin: the browser refuses it unless it is
            // Secure, Path=/ and has no Domain attribute, which means a sibling subdomain — or anything
            // that manages to set cookies for the registrable domain — cannot overwrite the session
            // cookie. Without the prefix that overwrite is a session-fixation primitive the server
            // cannot detect. Skipped when the operator has opted into insecure cookies, since every
            // prefix requires Secure and the browser would otherwise reject the cookie outright.
            if (!allowInsecureCookie)
            {
                if (string.IsNullOrEmpty(cookieDomain))
                {
                    options.Cookie.Name = "__Host-" + options.Cookie.Name;
                    options.Cookie.Path = "/";
                }
                else
                {
                    // A cookie domain rules out __Host-, which forbids Domain outright. __Secure- is
                    // the strongest prefix left: it drops the origin binding but still guarantees the
                    // cookie was set over HTTPS, which is what stops a sibling subdomain reached over
                    // plain HTTP from overwriting the session cookie.
                    options.Cookie.Name = "__Secure-" + options.Cookie.Name;
                }
            }

            options.Events.OnValidatePrincipal = async context =>
            {
                var stampClaim = context.Principal?.FindFirst("security_stamp")?.Value;
                var userId = context.Principal?.FindFirst("sub")?.Value
                    ?? context.Principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;

                if (userId is null || stampClaim is null)
                    return;

                // Absolute session expiration — reject sessions older than 7 days regardless of
                // sliding renewal, to prevent indefinite session extension.
                //
                // Measured against a stamp that renewal does not touch. It used to read
                // Properties.IssuedUtc, which sliding renewal rewrites: the cookie handler sets
                // _refreshIssuedUtc = now on every refresh and persists it as the ticket's new
                // IssuedUtc, and the handler below sets ShouldRenew = true after every security-stamp
                // revalidation — so the clock was reset at least every SecurityStampRevalidationMinutes
                // of activity. IssuedUtc could therefore never reach 7 days on a session that was used
                // at all, and this branch was dead code.
                if (CookieSignInHelper.SessionStartedAt(context.Properties) is { } sessionStarted &&
                    DateTimeOffset.UtcNow - sessionStarted > TimeSpan.FromDays(7))
                {
                    context.RejectPrincipal();
                    await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                    return;
                }

                var authOpts = context.HttpContext.RequestServices.GetRequiredService<IOptions<AuthOptions>>().Value;
                var lastValidated = context.Properties.GetString("stamp_validated");
                if (lastValidated is not null &&
                    DateTimeOffset.TryParse(lastValidated, out var lastTime) &&
                    DateTimeOffset.UtcNow - lastTime < TimeSpan.FromMinutes(authOpts.SecurityStampRevalidationMinutes))
                {
                    return;
                }

                var userStore = context.HttpContext.RequestServices.GetRequiredService<IUserStore>();
                var user = await userStore.GetAsync(userId);

                if (user is null || !user.IsActive || !string.Equals(user.SecurityStamp ?? "", stampClaim ?? "", StringComparison.Ordinal))
                {
                    context.RejectPrincipal();
                    await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                    return;
                }

                context.Properties.SetString("stamp_validated", DateTimeOffset.UtcNow.ToString("O"));
                context.ShouldRenew = true;
            };
        })
        .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
        {
            var issuer = configuration["Issuer"]!;

            // Expected audience(s) for resources protected by this scheme. When configured (Audience
            // and/or an Audiences array), tokens MUST carry a matching aud — so a token minted for a
            // different client in the same tenant can't be replayed against this resource. When unset,
            // fall back to "any audience present" (backward-compatible — but resource servers should
            // set Audience to close cross-client audience confusion).
            var expectedAudiences = new List<string>();
            if (configuration["Audience"] is { Length: > 0 } aud) expectedAudiences.Add(aud);
            expectedAudiences.AddRange(configuration.GetSection("Audiences").Get<string[]>() ?? []);

            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidIssuer = issuer,
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidAlgorithms = ["ES256"], // pin the signing alg (defence-in-depth vs alg confusion)
                // RFC 9068 §4: a resource server MUST reject a JWT whose typ is anything other than
                // at+jwt. Every token this issuer mints shares one key and one issuer, so without this
                // an id_token — minted for a browser, handed to the front end, and not a credential for
                // any API — passes issuer, audience, lifetime and algorithm validation on this scheme
                // and authenticates as its subject. The token-exchange path has pinned this since the
                // typ header was introduced; the host's own resource-server scheme had not, and it is
                // the one every consumer of this library gets by calling AddAuthagonal.
                ValidTypes = [Authagonal.Core.Constants.TokenTypes.AccessTokenJwt],
                ClockSkew = TimeSpan.FromSeconds(60),
                ValidateIssuerSigningKey = true
            };

            if (expectedAudiences.Count > 0)
                options.TokenValidationParameters.ValidAudiences = expectedAudiences;
            else
                options.TokenValidationParameters.AudienceValidator = (audiences, _, _) => audiences?.Any() == true;

            // Enforce access-token revocation: a token whose jti is in the revoked store is rejected
            // even though it is still cryptographically valid and unexpired.
            options.Events = new JwtBearerEvents
            {
                OnTokenValidated = async ctx =>
                {
                    var jti = ctx.Principal?.FindFirst("jti")?.Value;
                    if (!string.IsNullOrEmpty(jti))
                    {
                        var revoked = ctx.HttpContext.RequestServices.GetRequiredService<IRevokedTokenStore>();
                        if (await revoked.IsRevokedAsync(jti, ctx.HttpContext.RequestAborted))
                            ctx.Fail("Token has been revoked");
                    }
                }
            };
        })
        .AddScheme<AuthenticationSchemeOptions, ScimBearerAuthenticationHandler>("ScimBearer", null);

        // ---------------------------------------------------------------------------
        // Authorization
        // ---------------------------------------------------------------------------
        var adminScope = configuration["AdminApi:Scope"] ?? "authagonal-admin";

        services.AddAuthorization(options =>
        {
            options.AddPolicy("IdentityAdmin", policy =>
            {
                policy.AuthenticationSchemes.Add(JwtBearerDefaults.AuthenticationScheme);
                policy.RequireAuthenticatedUser();
                policy.RequireAssertion(context =>
                {
                    var scopeClaim = context.User.FindFirst("scope")
                        ?? context.User.FindFirst("scp");

                    if (scopeClaim is null)
                        return false;

                    var scopes = scopeClaim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    return scopes.Contains(adminScope, StringComparer.OrdinalIgnoreCase);
                });
            });

            options.AddPolicy("ScimProvisioning", policy =>
            {
                policy.AuthenticationSchemes.Add("ScimBearer");
                policy.RequireAuthenticatedUser();
                policy.RequireClaim("client_id");
            });
        });

        // ---------------------------------------------------------------------------
        // CORS
        // ---------------------------------------------------------------------------
        services.AddCors();
        services.AddSingleton<ICorsPolicyProvider, DynamicCorsPolicyProvider>();

        // ---------------------------------------------------------------------------
        // Cluster — leader election + cross-node event bus (pluggable backend).
        // Defaults to single-node in-process; the cloud swaps in the Azure-storage backend
        // via configureClustering (UseAzureStorage / UseAzureStorageBus).
        // ---------------------------------------------------------------------------
        services.AddAuthagonalClustering(configuration, configureClustering);

        return services;
    }

    /// <summary>
    /// Adds Authagonal middleware to the pipeline: exception handling, security headers,
    /// CORS, rate limiting, authentication, authorization, and static file serving.
    /// Call this before <see cref="MapAuthagonalEndpoints"/>.
    /// </summary>
    /// <summary>
    /// Fallback trusted-proxy ranges used for <c>X-Forwarded-For</c> only, when
    /// <c>ForwardedHeaders:KnownProxies</c> and <c>:KnownNetworks</c> are both unset.
    /// </summary>
    /// <remarks>
    /// Loopback plus the RFC1918 / link-local / ULA ranges a co-located proxy usually occupies. This is a
    /// guess about topology, and it is deliberately confined to the client IP: it beats the framework's
    /// empty-set behaviour (which honours the header from any caller at all) without ever being load
    /// bearing for a security decision. <c>X-Forwarded-Proto</c> is NOT honoured on the strength of it —
    /// see <c>UseAuthagonal</c> — because the scheme gates the OAuth endpoints, and on a flat network
    /// every neighbour is inside these ranges. Operators should declare their actual proxy.
    /// </remarks>
    private static readonly string[] DefaultTrustedProxyNetworks =
    [
        "127.0.0.0/8",     // loopback
        "::1/128",         // loopback (v6)
        "10.0.0.0/8",      // RFC1918
        "172.16.0.0/12",   // RFC1918
        "192.168.0.0/16",  // RFC1918
        "169.254.0.0/16",  // link-local
        "fc00::/7",        // unique local (v6)
        "fe80::/10",       // link-local (v6)
    ];

    public static WebApplication UseAuthagonal(this WebApplication app)
    {
        // The most common integrator trap: no email sender + the confirmed-email login gate
        // (default true) means self-registered users can never sign in. Warn loudly once. Resolve inside
        // a scope and swallow any failure: a tenant-scoped IEmailService (e.g. one whose dependencies are
        // resolved from a per-request tenant context) cannot be constructed at startup outside a request,
        // and this diagnostic must never crash the host.
        try
        {
            using var scope = app.Services.CreateScope();
            if (scope.ServiceProvider.GetService<IEmailService>() is NullEmailService)
                app.Logger.LogWarning(
                    "No email sender is configured: verification and password-reset emails will be DISCARDED, " +
                    "and self-registered users cannot log in while the confirmed-email login gate is on (the default). " +
                    "Set Email:ResendApiKey + Email:SenderEmail, register your own IEmailService before AddAuthagonal, " +
                    "or set Auth:AutoConfirmEmailDomains for your domain.");
        }
        catch
        {
            // IEmailService is tenant-scoped and not resolvable at startup — skip the diagnostic.
        }

        // The same trap one seam over, and until now without the warning. With no
        // SecretProvider:VaultUri the plaintext provider is registered and every secret it is handed
        // — upstream OIDC client secrets, SAML SP private keys, TOTP seeds, recovery codes — is
        // stored verbatim, so it is in the store in cleartext and in every backup taken of it. A
        // read-only storage credential or a leaked SAS is then enough to impersonate this tenant to
        // its upstream IdPs. That is the right default for development and it is documented, but the
        // operator who never read that line is exactly the one who needs telling.
        var secretProviderOptions = app.Services.GetService<SecretProviderOptions>() ?? new SecretProviderOptions();
        if (app.Services.GetService<ISecretProvider>() is PlaintextSecretProvider)
        {
            // Asking for vault references while the resolved provider has no vault behind it is not a
            // stricter posture, it is a dead deployment: every reference the plaintext provider stores
            // is unprefixed, so every resolve would throw at the first login that needed a secret.
            // Say so at startup rather than at 3am.
            if (secretProviderOptions.RequireVaultReferences)
                throw new InvalidOperationException(
                    "SecretProvider:RequireVaultReferences is set but the resolved ISecretProvider is " +
                    "PlaintextSecretProvider, which stores no vault references at all — every secret " +
                    "resolve would fail. Set SecretProvider:VaultUri (or register a vault-backed " +
                    "ISecretProvider before AddAuthagonal), or clear RequireVaultReferences.");

            if (!app.Environment.IsDevelopment())
                app.Logger.LogWarning(
                    "No secret provider is configured: secrets are stored in CLEARTEXT (the reference IS " +
                    "the value). Upstream OIDC client secrets, SAML SP private keys, TOTP seeds and recovery " +
                    "codes are readable by anyone who can read the store or a backup of it. Set " +
                    "SecretProvider:VaultUri for Azure Key Vault, call AddSecretsManager for AWS Secrets " +
                    "Manager, or register your own ISecretProvider before AddAuthagonal.");
        }

        // Forwarded-header trust. Two different questions ride on these headers, and they do not
        // deserve the same answer.
        //
        // X-Forwarded-For adjusts the CLIENT IP: the key that rate limiting, lockout and the /_internal
        // guard hang off. Taking it from something that turns out not to be a proxy degrades those
        // controls — but taking it from nobody degrades them too, and the framework's behaviour with an
        // empty trust set is worse than either, because an empty set means every caller is a trusted
        // proxy. A best-effort default earns its place on this header.
        //
        // X-Forwarded-Proto changes the SCHEME, and the scheme is what the RFC 6749 §3.1/§3.2 TLS gate
        // below rests on. A security control must not rest on an inference. This package ships on
        // nuget.org and cannot see the network it was deployed onto, so "the peer holds a private
        // address" is a guess about topology rather than knowledge of one: on a flat corporate LAN, a
        // shared VPC, or a shared container bridge, every neighbouring workload sits inside those ranges
        // and could assert https over a request that arrived in cleartext.
        //
        // Hence the split below. The fallback ranges may adjust the client IP, but only a trust set the
        // operator has DECLARED may change the scheme. A deployment whose proxy has no fixed address
        // declares ForwardedHeaders:KnownNetworks: ["0.0.0.0/0", "::/0"], which asserts the thing that
        // is actually true of it — nothing but the proxy can reach this process — rather than leaving
        // the library to infer that from an address range it cannot verify. That declaration is one
        // line, it is auditable in review, and it reads the same whatever the deployment stands on.
        var declaredProxies = new List<System.Net.IPAddress>();
        var declaredNetworks = new List<(System.Net.IPAddress Prefix, int Length)>();

        foreach (var proxy in app.Configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [])
        {
            if (System.Net.IPAddress.TryParse(proxy, out var ip))
                declaredProxies.Add(ip);
        }
        foreach (var network in app.Configuration.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>() ?? [])
        {
            var parts = network.Split('/');
            if (parts.Length == 2 && System.Net.IPAddress.TryParse(parts[0], out var prefix) &&
                int.TryParse(parts[1], out var prefixLength))
                declaredNetworks.Add((prefix, prefixLength));
        }

        var proxyTrustDeclared = declaredProxies.Count > 0 || declaredNetworks.Count > 0;

        var fhOptions = new ForwardedHeadersOptions
        {
            // The scheme is only ever taken from a proxy the operator named. With nothing declared this
            // reduces to XForwardedFor, and Request.Scheme is then whatever the connection actually was.
            ForwardedHeaders = proxyTrustDeclared
                ? Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
                  | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto
                : Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor,
            // Honour only the single hop the nearest proxy appends; ignore anything further left in the
            // chain, which the client controls.
            ForwardLimit = app.Configuration.GetValue("ForwardedHeaders:ForwardLimit", 1),
        };

        // Start from an empty trust set — the framework default of loopback-only would ignore forwarded
        // headers entirely behind a non-loopback ingress — and then populate it. Never leave it empty.
        fhOptions.KnownProxies.Clear();
#if NET10_0_OR_GREATER
        fhOptions.KnownIPNetworks.Clear();
#else
        fhOptions.KnownNetworks.Clear();
#endif

        void TrustNetwork(System.Net.IPAddress prefix, int length)
        {
#if NET10_0_OR_GREATER
            fhOptions.KnownIPNetworks.Add(new System.Net.IPNetwork(prefix, length));
#else
            fhOptions.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(prefix, length));
#endif
        }

        if (proxyTrustDeclared)
        {
            foreach (var ip in declaredProxies)
                fhOptions.KnownProxies.Add(ip);
            foreach (var (prefix, length) in declaredNetworks)
                TrustNetwork(prefix, length);
        }
        else
        {
            foreach (var cidr in DefaultTrustedProxyNetworks)
            {
                var parts = cidr.Split('/');
                TrustNetwork(System.Net.IPAddress.Parse(parts[0]), int.Parse(parts[1]));
            }

            app.Services.GetRequiredService<ILoggerFactory>()
                .CreateLogger("Authagonal.ForwardedHeaders")
                .LogWarning(
                    "ForwardedHeaders:KnownProxies/KnownNetworks is not configured. X-Forwarded-For will be " +
                    "honoured from the loopback and private ranges ({Networks}) so a public caller cannot forge " +
                    "the client IP — but X-Forwarded-Proto will be honoured from NOBODY, because the scheme " +
                    "decides whether /connect/* answers at all (RFC 6749 §3.1/§3.2), whether cookies are marked " +
                    "Secure, and whether the absolute URLs this server generates are https. Declare the proxy " +
                    "that terminates TLS in ForwardedHeaders:KnownNetworks (its CIDR) or " +
                    "ForwardedHeaders:KnownProxies (its address) — or [\"0.0.0.0/0\", \"::/0\"] if nothing but " +
                    "that proxy can reach this process, which states the assumption instead of inferring it.",
                    string.Join(", ", DefaultTrustedProxyNetworks));
        }

        // Record the untampered peer address BEFORE forwarded headers can overwrite it, so an
        // authorization decision can be made on the real peer rather than a client-supplied header.
        app.UseRawPeerAddressCapture();
        app.UseForwardedHeaders(fhOptions);

        // RFC 6749 §3.1 and §3.2: the authorization server MUST require TLS at the authorization and
        // the token endpoint, and §10.3 / RFC 6750 §5.3 say the same of everything a token then rides
        // in. Nothing in this pipeline enforced it. HSTS is emitted below but only on a request that
        // already arrived over TLS — which is what RFC 6797 §7.2 requires, a browser ignores the header
        // over plaintext — so it protects the NEXT request and does nothing for the one carrying the
        // authorization code, the client secret in a Basic header, or the tokens coming back.
        //
        // It stops being theoretical for a self-hoster who exposes a compose-style deployment directly,
        // or whose proxy also answers on :80: the whole code exchange completes in cleartext and the
        // server neither refuses nor says anything.
        //
        // Two placement facts this depends on. It runs AFTER the forwarded-headers registration above,
        // so Request.IsHttps is true in exactly two cases and no others: the connection to this process
        // was itself TLS, or a proxy the operator declared said the client's was. Those are the only two
        // ways a server can know, and the block above is what makes the second one a statement rather
        // than a guess. And it gates /connect/* specifically — authorize, token, par, revocation,
        // introspect, userinfo, deviceauthorization, endsession, register — which is the protocol
        // surface the RFCs name. /health and the internal endpoints stay open, because they are reached
        // over plain http by a load balancer or a sibling process before anything is in front of them,
        // and gating those trades a theoretical exposure for a real outage.
        //
        // Auth:AllowInsecureHttp turns it off for the deployments that genuinely speak http: the
        // docker-compose quickstart on http://localhost:8080, the custom-server demo, and the test
        // harness's TestServer. All three set it explicitly. It is never a default and never inferred
        // from the environment name — running plaintext is something an operator has to say out loud.
        var allowInsecureHttp = app.Services.GetService<IOptions<AuthOptions>>()?.Value.AllowInsecureHttp ?? false;
        if (allowInsecureHttp)
        {
            app.Logger.LogWarning(
                "Auth:AllowInsecureHttp is set: the OAuth endpoints will answer plaintext http requests. " +
                "Authorization codes, client secrets and access/refresh tokens are readable by anyone on " +
                "the network path. This is a development setting — never set it in production.");
        }
        else
        {
            var issuer = app.Configuration["Issuer"];
            if (Uri.TryCreate(issuer, UriKind.Absolute, out var issuerUri)
                && string.Equals(issuerUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            {
                app.Logger.LogWarning(
                    "Issuer '{Issuer}' uses the http scheme and Auth:AllowInsecureHttp is not set, so every " +
                    "plaintext request to /connect/* will be refused. Serve the issuer over https — terminating " +
                    "TLS at a proxy declared in ForwardedHeaders:KnownNetworks/KnownProxies satisfies this — or " +
                    "set Auth:AllowInsecureHttp if this deployment is deliberately plaintext.",
                    issuer);
            }
            else if (!proxyTrustDeclared)
            {
                // The shape that bites hardest on upgrade: TLS terminates at a proxy, the proxy sends
                // X-Forwarded-Proto: https, and nothing has declared that proxy — so the scheme stays
                // http and every /connect/* request is refused with a message about TLS on a deployment
                // the operator believes is already on TLS. Name the cause at startup rather than leaving
                // it to be inferred from a 400.
                app.Logger.LogWarning(
                    "No forwarded-proxy trust is declared, so X-Forwarded-Proto is ignored and /connect/* will " +
                    "answer 400 for any request that did not reach this process over TLS directly. If TLS " +
                    "terminates at a proxy in front of this server, declare it in " +
                    "ForwardedHeaders:KnownNetworks / ForwardedHeaders:KnownProxies.");
            }
            else if (app.Environment.IsDevelopment())
            {
                // The http-Issuer warning above catches a deployment configured for plaintext. It does NOT
                // catch the shape that actually bites a developer: an https Issuer (the shipped default is
                // https://localhost:5001) while the process is listening on a plain http port, because
                // `dotnet run` picks whichever launch profile comes first. Everything then looks correct and
                // /connect/* answers 400 with nothing in the log to connect it to. Say it here — Development
                // only, so it costs a production host nothing.
                app.Logger.LogInformation(
                    "TLS is required at /connect/* (RFC 6749 §3.1/§3.2). If you are serving this host over " +
                    "plain http locally, those endpoints will answer 400 — run the https launch profile, or " +
                    "set Auth:AllowInsecureHttp for local development.");
            }
        }

        app.UseExceptionHandlingMiddleware();

        // The TLS gate itself — see the RFC 6749 §3.1/§3.2 note above for why it exists and why it
        // sits exactly here: forwarded headers have just run, so Request.IsHttps reflects the scheme
        // the CLIENT used rather than the hop between the proxy and Kestrel.
        if (!allowInsecureHttp)
        {
            app.Use(async (context, next) =>
            {
                if (!context.Request.IsHttps
                    && context.Request.Path.StartsWithSegments("/connect", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    context.Response.ContentType = "application/json";

                    // A request that arrived in cleartext but carries X-Forwarded-Proto: https is the
                    // deployment saying "there IS a proxy in front of me and it did terminate TLS" while
                    // this server has no declaration that would let it believe that. Say so, because the
                    // remedy is a config line and the generic message sends the operator hunting for TLS
                    // they have already configured.
                    var claimedHttps = context.Request.Headers["X-Forwarded-Proto"]
                        .Any(v => string.Equals(v, "https", StringComparison.OrdinalIgnoreCase));

                    await context.Response.WriteAsync(claimedHttps
                        ? "{\"error\":\"invalid_request\",\"error_description\":\"TLS is required at the OAuth " +
                          "endpoints (RFC 6749 sections 3.1 and 3.2). This request carried X-Forwarded-Proto: " +
                          "https, but no proxy is declared as trusted to set it, so the claim was ignored. " +
                          "Declare the terminating proxy in ForwardedHeaders:KnownNetworks or " +
                          "ForwardedHeaders:KnownProxies, or set Auth:AllowInsecureHttp for a development " +
                          "deployment.\"}"
                        : "{\"error\":\"invalid_request\",\"error_description\":\"TLS is required at the OAuth " +
                          "endpoints (RFC 6749 sections 3.1 and 3.2). Use https, or set Auth:AllowInsecureHttp " +
                          "for a development deployment.\"}");
                    return;
                }

                await next();
            });
        }

        // Request localization
        var supportedCultures = new[] { "en", "zh-Hans", "de", "fr", "es", "vi", "pt" };
        app.UseRequestLocalization(options =>
        {
            options.SetDefaultCulture("en");
            options.AddSupportedCultures(supportedCultures);
            options.AddSupportedUICultures(supportedCultures);
            options.ApplyCurrentCultureToResponseHeaders = true;
        });

        // Security headers. When Cloudflare Turnstile is configured, the CSP must allow its
        // script and challenge iframe (challenges.cloudflare.com); otherwise the widget is
        // blocked and Turnstile-gated forms (login/register/forgot/reset) can never produce a
        // token, leaving their submit buttons permanently disabled. Tight default otherwise.
        var turnstileConfigured = !string.IsNullOrWhiteSpace(
            app.Services.GetService<IOptions<TurnstileOptions>>()?.Value.SiteKey);
        // base-uri and form-action are both present because neither falls back to default-src.
        //
        // Without base-uri, an injected <base> tag re-points every relative URL on the page — which on
        // the login app means the form posts, the script src and the API calls — while the CSP still
        // reads as locked down. Without form-action, an injected form posts credentials to any origin
        // the attacker chooses; script-src does not constrain a form target. These are the two
        // directives that matter most on a page whose whole job is to collect a password.
        const string cspTail = "; img-src 'self' data: https:; font-src 'self' data:; " +
            "style-src 'self' 'unsafe-inline'; frame-ancestors 'none'; object-src 'none'; " +
            "base-uri 'self'; form-action 'self'";

        var csp = turnstileConfigured
            ? "default-src 'self'; script-src 'self' https://challenges.cloudflare.com; " +
              "frame-src https://challenges.cloudflare.com" + cspTail
            : "default-src 'self'" + cspTail;

        app.Use(async (context, next) =>
        {
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["X-Frame-Options"] = "DENY";
            context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            context.Response.Headers["Content-Security-Policy"] = csp;
            if (context.Request.IsHttps)
            {
                context.Response.Headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
            }
            await next();
        });

        app.UseCors();
        app.UseAuthentication();
        app.UseAuthorization();

        app.UseDefaultFiles();
        app.UseStaticFiles();

        return app;
    }

    /// <summary>
    /// Maps all Authagonal endpoints: OAuth/OIDC, SAML, authentication, and admin APIs.
    /// Call after <see cref="UseAuthagonal"/>.
    /// </summary>
    public static WebApplication MapAuthagonalEndpoints(this WebApplication app)
    {
        // Anonymous, but no longer a free storage query per request.
        //
        // Each call ran a live store probe, so an unauthenticated caller could drive database load
        // with a trivially cheap request — and the response is cached briefly so a load balancer
        // polling every second does not multiply that by every replica. Liveness does not change
        // faster than this window.
        app.MapHealthChecks("/health", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
        {
            ResultStatusCodes =
            {
                [Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy] = StatusCodes.Status200OK,
                [Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded] = StatusCodes.Status200OK,
                [Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable,
            },
        })
        .AllowAnonymous()
        .AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "public, max-age=5";
            return await next(context);
        });

        app.MapDiscoveryEndpoints();
        app.MapJwksEndpoint();
        app.MapAuthorizeEndpoint();
        app.MapConsentEndpoints();
        app.MapAgentConsentEndpoints();
        app.MapApprovalEndpoints();
        app.MapTokenEndpoint();
        app.MapRevocationEndpoint();
        app.MapIntrospectionEndpoint();
        app.MapBackChannelLogoutEndpoints();
        app.MapDeviceAuthorizationEndpoints();
        app.MapEndSessionEndpoint();
        app.MapUserinfoEndpoint();
        app.MapClientRegistrationEndpoint();
        app.MapProtocolPushedAuthorizationEndpoint();

        if (app.Configuration.GetValue("AdminApi:Enabled", true))
        {
            app.MapUserAdminEndpoints();
            app.MapRoleAdminEndpoints();
            app.MapScopeAdminEndpoints();
            app.MapClientAdminEndpoints();
            app.MapProvisioningAdminEndpoints();
            app.MapSsoAdminEndpoints();
            app.MapTokenAdminEndpoints();
            app.MapMfaAdminEndpoints();
            app.MapScimTokenAdminEndpoints();
            app.MapAgentAdminEndpoints();
        }

        app.MapScimUserEndpoints();
        app.MapScimGroupEndpoints();
        app.MapScimDiscoveryEndpoints();

        app.MapAuthEndpoints();
        app.MapMfaEndpoints();
        app.MapMfaSetupEndpoints();
        app.MapSamlEndpoints();
        app.MapOidcEndpoints();

        return app;
    }
}

/// <summary>
/// Wires IKeyManager into JWT bearer token validation at runtime.
/// Resolves IKeyManager per-request via IHttpContextAccessor to support scoped (multi-tenant) lifetimes.
/// </summary>
public sealed class JwtBearerKeyResolverPostConfigure(IServiceProvider rootProvider) : IPostConfigureOptions<JwtBearerOptions>
{
    public void PostConfigure(string? name, JwtBearerOptions options)
    {
        if (name != JwtBearerDefaults.AuthenticationScheme)
            return;

        options.TokenValidationParameters.IssuerSigningKeyResolver = (_, securityToken, _, _) =>
        {
            var httpContextAccessor = rootProvider.GetRequiredService<IHttpContextAccessor>();
            var sp = httpContextAccessor.HttpContext?.RequestServices ?? rootProvider;
            var keyManager = sp.GetRequiredService<Authagonal.Core.Services.IKeyManager>();
            return keyManager.GetSecurityKeys()
                .Select(Authagonal.Protocol.Services.ProtocolSigningKeyOps.JwkToSecurityKey)
                .ToList();
        };
    }
}
