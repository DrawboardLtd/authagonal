using Authagonal.Core.Models;
using Authagonal.Core.Stores;
using Authagonal.Core.Services;

namespace Authagonal.Server.Services;

public sealed class ClientSeedService(
    IClientStore clientStore,
    PasswordHasher passwordHasher,
    IConfiguration configuration,
    ILogger<ClientSeedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        var clients = configuration.GetSection("Clients").Get<List<ClientSeedConfig>>() ?? [];

        // Also support a single "SeedClient" section for simple demos
        var singleClient = configuration.GetSection("SeedClient").Get<ClientSeedConfig>();
        if (singleClient is not null && !string.IsNullOrWhiteSpace(singleClient.EffectiveId))
        {
            clients.Add(singleClient);
        }

        if (clients.Count == 0)
        {
            logger.LogDebug("No client seed configuration found");
            return;
        }

        foreach (var seed in clients)
        {
            var clientId = seed.EffectiveId;
            if (string.IsNullOrWhiteSpace(clientId))
            {
                logger.LogWarning("Skipping client seed entry with missing Id");
                continue;
            }

            // Read-merge-write, matching ScopeSeedService ("a field omitted from the seed preserves
            // the stored value") and RoleSeedService ("deliberately additive… an operator granting a
            // role through the admin API must not have it taken away by the next restart").
            //
            // This seeder alone built a fresh OAuthClient and Replace-d the row, so every property the
            // seed does not state reverted to the MODEL DEFAULT on each pod start — silently undoing
            // admin hardening applied through PUT /api/v1/clients/{id}. Enabled is the sharpest: the
            // seeder never sets it and the model defaults it to true, so restarting re-enabled a client
            // an operator had deliberately disabled. RequireConsent, RequirePushedAuthorizationRequests,
            // Audiences, JwksJson/JwksUri (which kills private_key_jwt for an agent client) and any
            // secret rotated through the admin API went the same way.
            var existing = await clientStore.GetAsync(clientId, ct);

            // A seed silent about scopes preserves the stored list rather than clearing it, like every
            // other field below. The reserved-scope checks run on the list that will actually be
            // written, so a preserved list is checked on every boot too.
            var seededScopes = seed.Scopes ?? seed.AllowedScopes ?? existing?.AllowedScopes ?? [];

            // The admin API and dynamic registration both refuse to grant a client the administrative
            // scope; seeding applied no check at all, so configuration could hand a client the very thing
            // those two paths exist to withhold. Skip the whole entry rather than silently dropping the
            // scope — a seed that asks for this is a misconfiguration the operator needs to see.
            var adminScope = configuration["AdminApi:Scope"] ?? AdminScopeReservation.DefaultAdminScope;
            if (AdminScopeReservation.Grants(seededScopes, adminScope))
            {
                logger.LogError(
                    "Refusing to seed client {Id}: it requests the reserved administrative scope '{Scope}'. " +
                    "No client may hold it — a client_credentials client that did could mint admin tokens indefinitely.",
                    clientId, adminScope);
                continue;
            }

            // A scope entry containing whitespace expands into several scopes downstream, which is how the
            // reservation above was bypassed. Reject rather than normalize: the intent is ambiguous.
            if (AdminScopeReservation.FindMalformedScope(seededScopes) is { } malformed)
            {
                logger.LogError(
                    "Refusing to seed client {Id}: scope entry '{Scope}' is not a single scope token. " +
                    "Scope names cannot contain whitespace — list each scope separately.",
                    clientId, malformed);
                continue;
            }

            // Build secret hashes: use explicit hashes if provided, otherwise hash plaintext secret
            var secretHashes = seed.SecretHashes ?? [];
            if (secretHashes.Count == 0 && !string.IsNullOrWhiteSpace(seed.ClientSecret))
            {
                secretHashes = [passwordHasher.HashPassword(seed.ClientSecret)];
            }

            var client = existing is null
                ? new OAuthClient { ClientId = clientId }
                : existing;

            client.ClientId = clientId;
            client.ClientName = seed.Name ?? seed.ClientName ?? existing?.ClientName ?? clientId;
            // Only overwritten when the seed actually supplies one, so a rotation through the admin
            // API survives the next restart.
            if (secretHashes.Count > 0) client.ClientSecretHashes = secretHashes;
            client.AllowedGrantTypes = seed.GrantTypes ?? seed.AllowedGrantTypes ?? existing?.AllowedGrantTypes ?? [];
            client.RedirectUris = seed.RedirectUris ?? existing?.RedirectUris ?? [];
            client.PostLogoutRedirectUris = seed.PostLogoutRedirectUris ?? existing?.PostLogoutRedirectUris ?? [];
            client.AllowedScopes = seededScopes;
            client.AllowedCorsOrigins = seed.CorsOrigins ?? seed.AllowedCorsOrigins ?? existing?.AllowedCorsOrigins ?? [];

            // Audiences: this seeder had NO field for them, so the one thing the authorize path's own
            // justification claims about seed configuration — that "every surface that creates a client
            // (dynamic registration, the admin API, seed configuration) does accept audiences" — was not
            // true of the Server host at all. An operator could not declare a client's audiences in
            // configuration, only through the admin API, which meant a config-seeded client kept the legacy
            // permissive "may name any absolute URI" reading with no way to tighten it.
            //
            // Validated on the list that will actually be written, and only overwritten when the seed states
            // one, matching every other field here. Declaring by naming, as the admin API does.
            if (seed.Audiences is { Count: > 0 } seededAudiences)
            {
                if (Core.Services.ResourceAudiencePolicy.RejectAudiences(seededAudiences) is { } audienceError)
                {
                    logger.LogError(
                        "Refusing to seed client {Id}: {Error}. An audience becomes the `aud` of a signed "
                        + "token, so it must be an absolute URI within the documented caps.",
                        clientId, audienceError);
                    continue;
                }

                client.Audiences = seededAudiences;
                client.AudiencesDeclared = true;
            }
            client.RequirePkce = seed.RequirePkce ?? existing?.RequirePkce ?? true;
            client.AllowOfflineAccess = seed.AllowOfflineAccess ?? existing?.AllowOfflineAccess ?? false;
            client.RequireClientSecret = seed.RequireSecret ?? seed.RequireClientSecret ?? existing?.RequireClientSecret ?? true;
            client.AlwaysIncludeUserClaimsInIdToken = seed.AlwaysIncludeUserClaimsInIdToken ?? existing?.AlwaysIncludeUserClaimsInIdToken ?? false;
            client.AccessTokenLifetimeSeconds = seed.AccessTokenLifetimeSeconds ?? existing?.AccessTokenLifetimeSeconds ?? 1800;
            client.IdentityTokenLifetimeSeconds = seed.IdentityTokenLifetimeSeconds ?? existing?.IdentityTokenLifetimeSeconds ?? 300;
            client.AuthorizationCodeLifetimeSeconds = seed.AuthorizationCodeLifetimeSeconds ?? existing?.AuthorizationCodeLifetimeSeconds ?? 300;
            client.AbsoluteRefreshTokenLifetimeSeconds = seed.AbsoluteRefreshTokenLifetimeSeconds ?? existing?.AbsoluteRefreshTokenLifetimeSeconds ?? 2592000;
            client.SlidingRefreshTokenLifetimeSeconds = seed.SlidingRefreshTokenLifetimeSeconds ?? existing?.SlidingRefreshTokenLifetimeSeconds ?? 1296000;
            client.RefreshTokenUsage = seed.RefreshTokenUsage ?? existing?.RefreshTokenUsage ?? RefreshTokenUsage.OneTime;
            client.MfaPolicy = seed.MfaPolicy ?? existing?.MfaPolicy ?? MfaPolicy.Disabled;
            client.BackChannelLogoutUri = seed.BackChannelLogoutUri ?? existing?.BackChannelLogoutUri;
            // Continue-to-app affordances: without these bound, config-seeded tenants have an
            // empty /apps and every post-auth continuation collapses to '/' on the auth host.
            client.InitiateLoginUri = seed.InitiateLoginUri ?? existing?.InitiateLoginUri;
            client.ClientUri = seed.ClientUri ?? existing?.ClientUri;
            client.IsDefaultApplication = seed.IsDefaultApplication ?? existing?.IsDefaultApplication ?? false;

            await clientStore.UpsertAsync(client, ct);
            logger.LogInformation("Seeded client {Id} ({Name})", client.ClientId, client.ClientName);
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    public sealed class ClientSeedConfig
    {
        // Client identity — supports both "Id" (compact) and "ClientId" (demo-friendly)
        public string? Id { get; set; }
        public string? ClientId { get; set; }
        internal string? EffectiveId => ClientId ?? Id;

        public string? Name { get; set; }
        public string? ClientName { get; set; }

        // Secrets — either pre-hashed or plaintext (auto-hashed on startup)
        /// <summary>
        /// Resource identifiers this client may name as a <c>resource</c>, and what its tokens' <c>aud</c>
        /// may be narrowed to. Absent preserves the stored list; naming any marks the client as having
        /// DECLARED its audiences, which is what makes <c>ResourceAudiencePolicy</c> stop reading it as a
        /// legacy client that may name anything absolute.
        /// </summary>
        public List<string>? Audiences { get; set; }

        public List<string>? SecretHashes { get; set; }
        public string? ClientSecret { get; set; }

        // Grant types — "GrantTypes" (compact) or "AllowedGrantTypes" (demo-friendly)
        public List<string>? GrantTypes { get; set; }
        public List<string>? AllowedGrantTypes { get; set; }

        public List<string>? RedirectUris { get; set; }
        public List<string>? PostLogoutRedirectUris { get; set; }

        // Scopes — "Scopes" (compact) or "AllowedScopes" (demo-friendly)
        public List<string>? Scopes { get; set; }
        public List<string>? AllowedScopes { get; set; }

        // CORS — "CorsOrigins" (compact) or "AllowedCorsOrigins" (demo-friendly)
        public List<string>? CorsOrigins { get; set; }
        public List<string>? AllowedCorsOrigins { get; set; }

        public bool? RequirePkce { get; set; }
        public bool? AllowOfflineAccess { get; set; }
        public bool? RequireSecret { get; set; }
        public bool? RequireClientSecret { get; set; }
        public bool? AlwaysIncludeUserClaimsInIdToken { get; set; }
        public int? AccessTokenLifetimeSeconds { get; set; }
        public int? IdentityTokenLifetimeSeconds { get; set; }
        public int? AuthorizationCodeLifetimeSeconds { get; set; }
        public int? AbsoluteRefreshTokenLifetimeSeconds { get; set; }
        public int? SlidingRefreshTokenLifetimeSeconds { get; set; }
        public RefreshTokenUsage? RefreshTokenUsage { get; set; }
        public MfaPolicy? MfaPolicy { get; set; }

        // Continue-to-app affordances (the /apps allow-list + the login SPA's continue button).
        public string? InitiateLoginUri { get; set; }
        public string? ClientUri { get; set; }
        public bool? IsDefaultApplication { get; set; }
        /// <summary>Where OIDC back-channel logout tokens for this client are POSTed
        /// (e.g. a BFF's <c>/bff/backchannel-logout</c>).</summary>
        public string? BackChannelLogoutUri { get; set; }
    }
}
