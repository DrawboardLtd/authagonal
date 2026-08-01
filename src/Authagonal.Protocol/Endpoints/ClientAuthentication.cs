using System.Collections.Concurrent;
using System.Text;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.Protocol.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Authagonal.Protocol.Endpoints;

/// <summary>
/// OAuth client authentication shared by the token, PAR, and (Server-side) device endpoints:
/// <c>private_key_jwt</c> (RFC 7523 client assertion, when the request carries one), then
/// client_secret_basic with a client_secret_post fallback, then the standard
/// lookup → enabled → secret-verification sequence.
/// </summary>
internal static class ClientAuthentication
{
    private const string JwtBearerAssertionType = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";
    private static readonly TimeSpan MaxAssertionLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan JwksCacheTtl = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The <c>token_endpoint_auth_method</c> values this code actually implements — the single source
    /// for both discovery documents and for what dynamic registration will accept.
    /// </summary>
    /// <remarks>
    /// Kept here rather than restated at each site because it was restated at each site: the two
    /// discovery endpoints drifted from one another, and registration validated against nothing at all,
    /// so a client could register <c>private_key_jwt</c> and silently be issued a client_secret instead.
    /// </remarks>
    public static readonly string[] SupportedAuthMethods =
        ["client_secret_basic", "client_secret_post", "private_key_jwt", "none"];

    /// <summary>
    /// Algorithms accepted on a <c>private_key_jwt</c> client assertion, and advertised as
    /// <c>token_endpoint_auth_signing_alg_values_supported</c>.
    /// </summary>
    /// <remarks>
    /// Pinned rather than accepting whatever the assertion header asks for. Keys come from the client's
    /// registered JWKS and never from a jku/jwk/x5u header (IdentityModel does not resolve those), so
    /// this is not the classic RS/HS confusion — but an explicit list means a symmetric key appearing in
    /// a client's JWKS cannot quietly turn client authentication into an HMAC over a value the client
    /// also publishes. RFC 7518 asymmetric algorithms only.
    /// </remarks>
    public static readonly string[] SupportedAssertionAlgorithms =
    [
        SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSha384, SecurityAlgorithms.RsaSha512,
        SecurityAlgorithms.RsaSsaPssSha256, SecurityAlgorithms.RsaSsaPssSha384, SecurityAlgorithms.RsaSsaPssSha512,
        SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.EcdsaSha384, SecurityAlgorithms.EcdsaSha512,
    ];

    // Config-derived public key material only — identical on every pod, safe to cache in-memory.
    private static readonly ConcurrentDictionary<string, (JsonWebKeySet Keys, DateTimeOffset FetchedAt)> JwksCache = new();

    /// <summary>
    /// Ceiling on distinct <c>jwks_uri</c> values held in <see cref="JwksCache"/> at once.
    /// </summary>
    /// <remarks>
    /// The cache is a process-wide static keyed by a URL a client record supplies, and dynamic client
    /// registration lets an anonymous caller create those records — so without a bound it grows with the
    /// number of distinct URIs ever fetched and never shrinks, an unbounded process-lifetime allocation
    /// an attacker drives. Well past any real deployment's client count; the eviction below only ever
    /// fires under abuse.
    /// </remarks>
    private const int MaxCachedJwks = 512;

    /// <summary>
    /// Used only when the host did not register the named client (a host that composes its own
    /// container). No redirect following, and a bounded timeout — the same handler policy as every named
    /// outbound client the Server host registers. jwks_uri is checked once, before the request, so an
    /// automatic 302 would reach somewhere the check never saw;
    /// <see cref="Core.Services.SafeOutboundHttp"/> resolves the hops itself so it can re-run the guard
    /// on each one, which it can only do if the handler hands it the 3xx instead of chasing it.
    /// </summary>
    private static readonly HttpClient FallbackHttpClient =
        new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };
    public static (string? ClientId, string? ClientSecret) ExtractClientCredentials(
        HttpContext httpContext, IFormCollection form)
    {
        // Try Authorization header first (client_secret_basic)
        var authHeader = httpContext.Request.Headers.Authorization.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(authHeader) && authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var encoded = authHeader["Basic ".Length..];
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                var colonIndex = decoded.IndexOf(':');
                if (colonIndex > 0)
                {
                    var id = Uri.UnescapeDataString(decoded[..colonIndex]);
                    var secret = Uri.UnescapeDataString(decoded[(colonIndex + 1)..]);
                    return (id, secret);
                }
            }
            catch (FormatException)
            {
                // Malformed Basic header; fall through to form body
            }
        }

        // Fall back to client_secret_post (form body)
        return (form["client_id"].FirstOrDefault(), form["client_secret"].FirstOrDefault());
    }

    /// <summary>
    /// Authenticates the calling client. <paramref name="error"/> maps (error, description) to the
    /// endpoint's error result — endpoints differ on status codes (the token endpoint returns 400
    /// for a disabled client, PAR returns 401), so the mapping stays with the caller.
    /// </summary>
    /// <summary>
    /// True for the grant RFC 6749 §4.4 restricts to confidential clients, so a public client must be
    /// refused even though it is otherwise "authenticated" by presenting its (public) client_id.
    /// </summary>
    /// <remarks>
    /// Scoped to <c>client_credentials</c>: that is the explicit MUST. Token exchange is not
    /// restricted this way by RFC 8693, and public clients legitimately exchange tokens here (the
    /// BFF's context-bound exchange routes do exactly that), so refusing it would break a supported
    /// flow rather than close a hole.
    /// </remarks>
    private static bool RequiresConfidentialClient(string? grantType) =>
        grantType is Authagonal.Core.Constants.GrantTypes.ClientCredentials;

    /// <param name="requireAuthenticatedClient">
    /// When true, a client that presents neither a client assertion nor a secret is refused rather
    /// than accepted on its client_id alone. Set by endpoints RFC 7662 §2.1 requires to be
    /// authenticated regardless of how the client is registered.
    /// </param>
    public static async Task<(OAuthClient? Client, IResult? Error)> AuthenticateAsync(
        HttpContext httpContext,
        IFormCollection form,
        IClientStore clientStore,
        IClientSecretVerifier secretVerifier,
        Func<string, string, IResult> error,
        CancellationToken ct,
        bool requireAuthenticatedClient = false)
    {
        // Read from the form rather than taken as a parameter so revocation and introspection, which
        // carry no grant_type, are unaffected.
        var grantType = form["grant_type"].FirstOrDefault();

        // private_key_jwt: a client assertion, when present, is the whole authentication —
        // it never falls back to the secret path (a failed assertion must not become a
        // weaker-credential retry).
        var assertionType = form["client_assertion_type"].FirstOrDefault();
        var assertion = form["client_assertion"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(assertionType) || !string.IsNullOrWhiteSpace(assertion))
        {
            if (assertionType != JwtBearerAssertionType)
                return (null, error("invalid_client", $"client_assertion_type must be '{JwtBearerAssertionType}'"));
            if (string.IsNullOrWhiteSpace(assertion))
                return (null, error("invalid_client", "client_assertion is required"));
            return await AuthenticateWithAssertionAsync(
                httpContext, form, assertion, clientStore, error, ct);
        }

        var (clientId, clientSecret) = ExtractClientCredentials(httpContext, form);

        if (string.IsNullOrWhiteSpace(clientId))
            return (null, error("invalid_client", "client_id is required"));

        var client = await clientStore.GetAsync(clientId, ct);
        if (client is null)
            return (null, error("invalid_client", "Unknown client"));

        if (!client.Enabled)
            return (null, error("unauthorized_client", "Client is disabled"));

        // RFC 6749 §4.4: the client-credentials grant "MUST only be used by confidential clients",
        // and §4.4.2 requires the client to authenticate per §3.2.1. Nothing enforced that: a client
        // with RequireClientSecret=false and client_credentials in AllowedGrantTypes authenticated on
        // a bare client_id, and neither token endpoint nor HandleClientCredentialsAsync re-checked.
        // NOTE: this comment used to claim "Token exchange is refused on the same grounds". It is not,
        // and never was — see the remarks on RequiresConfidentialClient, which explain why that is
        // deliberate (RFC 8693 imposes no such restriction, and the BFF's context-bound exchange routes
        // are public clients doing exactly this). Two comments in one file disagreeing about what the
        // code does is how a reviewer concludes a control exists when it does not, so the false one is
        // corrected rather than left as aspiration.
        //
        // Only when the client is actually configured for the grant — a client that does not hold
        // client_credentials at all should still fail with the grant-type error, which says something
        // more useful than "you are public".
        if (!client.RequireClientSecret
            && RequiresConfidentialClient(grantType)
            && client.AllowedGrantTypes.Contains(Authagonal.Core.Constants.GrantTypes.ClientCredentials, StringComparer.Ordinal))
        {
            return (null, error("invalid_client",
                $"The {grantType} grant requires a confidential client; '{client.ClientId}' is registered as public"));
        }

        // A public client proves nothing by naming itself, so on an endpoint that must be
        // authenticated it is refused here rather than sliding past the block below.
        if (requireAuthenticatedClient && !client.RequireClientSecret)
            return (null, error("invalid_client", "This endpoint requires client authentication"));

        if (client.RequireClientSecret)
        {
            if (string.IsNullOrWhiteSpace(clientSecret))
                return (null, error("invalid_client", "client_secret is required"));

            // Bound the guesses BEFORE spending the hash. Verification runs a ~100k-iteration PBKDF2 per
            // attempt on an endpoint reachable without any credential, so unthrottled it is both an
            // unbounded offline-strength-per-request guessing oracle against the client secret and a CPU
            // amplifier: one request per core saturates the host. Keyed per client so one client's traffic
            // cannot lock out another's, and resolved through the DI seam so a host with a distributed
            // limiter gets a cluster-wide bound.
            var limiter = httpContext.RequestServices.GetService<IRateLimiter>();
            if (limiter is not null &&
                await limiter.IsRateLimitedAsync($"client-secret|{client.ClientId}", 30, TimeSpan.FromMinutes(1), ct))
                return (null, error("invalid_client", "Too many authentication attempts"));

            if (!await secretVerifier.VerifyAsync(client, clientSecret, ct))
                return (null, error("invalid_client", "Invalid client credentials"));
        }

        return (client, null);
    }

    /// <summary>
    /// RFC 7523 §3: the assertion must be signed by a key in the client's registered JWKS,
    /// with iss = sub = client_id, an audience naming this server, a bounded exp, and a jti
    /// that has not been seen before (replay cache rides <see cref="IRevokedTokenStore"/>
    /// when the host registers one).
    /// </summary>
    private static async Task<(OAuthClient? Client, IResult? Error)> AuthenticateWithAssertionAsync(
        HttpContext httpContext,
        IFormCollection form,
        string assertion,
        IClientStore clientStore,
        Func<string, string, IResult> error,
        CancellationToken ct)
    {
        JsonWebToken unvalidated;
        try
        {
            unvalidated = new JsonWebToken(assertion);
        }
        catch (ArgumentException)
        {
            return (null, error("invalid_client", "client_assertion is not a valid JWT"));
        }

        var clientId = unvalidated.Issuer;
        if (string.IsNullOrWhiteSpace(clientId))
            return (null, error("invalid_client", "client_assertion carries no issuer"));

        // An explicit client_id form field must agree with the assertion's issuer.
        var formClientId = form["client_id"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(formClientId) &&
            !string.Equals(formClientId, clientId, StringComparison.Ordinal))
            return (null, error("invalid_client", "client_id does not match the client_assertion issuer"));

        var client = await clientStore.GetAsync(clientId, ct);
        if (client is null)
            return (null, error("invalid_client", "Unknown client"));
        if (!client.Enabled)
            return (null, error("unauthorized_client", "Client is disabled"));

        var jwks = await ResolveClientJwksAsync(httpContext, client, ct);
        if (jwks is null || jwks.Keys.Count == 0)
            return (null, error("invalid_client", "Client has no registered JWKS for private_key_jwt"));

        // RFC 7523 audience: the token endpoint URL, with the issuer identifier accepted for interop.
        var tenantContext = httpContext.RequestServices.GetRequiredService<ITenantContext>();
        var issuer = tenantContext.Issuer;
        var validAudiences = new[]
        {
            issuer,
            $"{issuer}/connect/token",
            $"{issuer.TrimEnd('/')}{httpContext.Request.Path.Value}",
        };

        var validated = await new JsonWebTokenHandler().ValidateTokenAsync(assertion, new TokenValidationParameters
        {
            ValidIssuer = clientId,
            ValidateIssuer = true,
            ValidAudiences = validAudiences,
            ValidateAudience = true,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            IssuerSigningKeys = jwks.GetSigningKeys(),
            ValidateIssuerSigningKey = true,
            // See SupportedAssertionAlgorithms — the same list discovery advertises, so what a client
            // is told it may sign with is what this validator will accept.
            ValidAlgorithms = SupportedAssertionAlgorithms,
            ClockSkew = TimeSpan.FromSeconds(60),
        });
        if (!validated.IsValid)
            return (null, error("invalid_client", "client_assertion validation failed"));

        if (!string.Equals(unvalidated.Subject, clientId, StringComparison.Ordinal))
            return (null, error("invalid_client", "client_assertion sub must equal its iss (the client_id)"));

        var expiresAt = unvalidated.ValidTo;
        if (expiresAt == DateTime.MinValue || expiresAt > DateTime.UtcNow + MaxAssertionLifetime)
            return (null, error("invalid_client",
                $"client_assertion exp must be present and within {(int)MaxAssertionLifetime.TotalMinutes} minutes"));

        var jti = unvalidated.Id;
        if (string.IsNullOrWhiteSpace(jti))
            return (null, error("invalid_client", "client_assertion must carry a jti"));

        // Replay: each assertion is single-use for its lifetime.
        //
        // Fails closed when no store is registered. This block used to be skipped in silence, so a
        // host with no IRevokedTokenStore accepted private_key_jwt with NO single-use enforcement at
        // all — a captured assertion stayed replayable for its whole lifetime — while discovery went
        // on advertising private_key_jwt in token_endpoint_auth_methods_supported. OIDC Core §9 makes
        // single use a requirement of that method, so a host that cannot enforce it must refuse the
        // method rather than quietly offer a weaker version of it. AddAuthagonalProtocol registers no
        // store; the Azure, AWS and SQL provider packages all do.
        var replayCache = httpContext.RequestServices.GetService<IRevokedTokenStore>();
        if (replayCache is null)
        {
            return (null, error("invalid_client",
                "private_key_jwt requires a replay store; this deployment has no IRevokedTokenStore registered"));
        }

        {
            // One atomic claim, not a read followed by a write.
            //
            // This was IsRevokedAsync then AddAsync, over backends whose AddAsync is an unconditional
            // upsert — so two requests carrying the same assertion both read "not seen", both wrote,
            // and both authenticated. Single-use held only for sequential presentations, which is not
            // what an attacker who captured an assertion does with it.
            //
            // The key is hashed rather than composed from the raw jti. The jti is a claim the client
            // controls entirely and it was used verbatim as a storage key: on Azure Table a RowKey
            // containing '/', '\\', '#', '?', a control character, or over 1024 characters is a 400
            // that neither store path handled. Hashing makes the key fixed-width and charset-safe on
            // every backend, and gives up nothing — the value is never read back, only matched.
            if (!await replayCache.TryClaimOnceAsync(ReplayKey(clientId, jti), expiresAt, clientId, ct))
                return (null, error("invalid_client", "client_assertion has already been used"));
        }

        return (client, null);
    }

    /// <summary>
    /// The single-use ledger key for one client's assertion jti: fixed-width lowercase hex, so no
    /// backend's key charset or length limit is reachable from a client-supplied claim.
    /// </summary>
    private static string ReplayKey(string clientId, string jti) =>
        "ca-" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes($"client_assertion:{clientId}:{jti}")));

    /// <summary>Inline JWKS wins; a JwksUri is fetched through the host's HttpClient factory
    /// (or a shared fallback) and cached for <see cref="JwksCacheTtl"/>.</summary>
    /// <summary>
    /// How long a cached JWKS may still be served after the client's endpoint stops responding.
    /// </summary>
    /// <remarks>
    /// Long enough to ride out an ordinary outage, short enough that a client which rotated away from
    /// a compromised key and retired its old endpoint stops being accepted under that key.
    /// </remarks>
    private static readonly TimeSpan MaxJwksStaleness = TimeSpan.FromHours(24);

    private static async Task<JsonWebKeySet?> ResolveClientJwksAsync(
        HttpContext httpContext, OAuthClient client, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(client.JwksJson))
        {
            try
            {
                return new JsonWebKeySet(client.JwksJson);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        // https-only was the ONLY check here: nothing stopped an admin- or DCR-registered jwks_uri from
        // naming an internal address, and this fetch is reachable from an anonymous /connect/token request
        // (RFC 9700 §4.14 — SSRF from server-fetched client metadata). Every other outbound URL in the
        // product goes through this guard; this path was the one that did not.
        if (string.IsNullOrWhiteSpace(client.JwksUri) ||
            !Uri.TryCreate(client.JwksUri, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !Authagonal.Core.Services.OutboundUrl.IsSafe(client.JwksUri))
            return null;

        if (JwksCache.TryGetValue(client.JwksUri, out var cached) &&
            DateTimeOffset.UtcNow - cached.FetchedAt < JwksCacheTtl)
            return cached.Keys;

        try
        {
            var http = httpContext.RequestServices.GetService<IHttpClientFactory>()?.CreateClient("AuthagonalJwks")
                ?? FallbackHttpClient;

            // Through SafeOutboundHttp, not a raw GetStringAsync. IsSafe above only ever sees the URL
            // the client registered — and an HttpClient follows redirects on its own, so a jwks_uri on
            // an attacker-controlled https host answering `302 Location: https://169.254.169.254/…`
            // reached an address the guard had refused, with the guard none the wiser. Same reason the
            // SAML metadata and OIDC discovery fetches go through it. It also bounds the response,
            // which matters here because the trigger is an anonymous /connect/token request.
            var json = await Authagonal.Core.Services.SafeOutboundHttp.GetStringAsync(http, client.JwksUri, ct: ct);
            var keys = new JsonWebKeySet(json);
            EvictExpiredJwks();
            JwksCache[client.JwksUri] = (keys, DateTimeOffset.UtcNow);
            return keys;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or ArgumentException
                                      or InvalidOperationException)
        {
            // A stale cached set beats an outage-shaped auth failure — but only for a bounded time.
            //
            // The fallback had no age limit, so an unreachable jwks_uri meant the last-seen keys were
            // served forever. That inverts what key rotation is for: a client that rotates BECAUSE its
            // key was compromised, then takes its old JWKS endpoint down, leaves this server accepting
            // assertions signed by the compromised key indefinitely. Past the bound, authentication
            // fails loudly instead, which is a diagnosable outage rather than a silent one.
            if (JwksCache.TryGetValue(client.JwksUri, out var stale) &&
                DateTimeOffset.UtcNow - stale.FetchedAt < MaxJwksStaleness)
            {
                return stale.Keys;
            }

            return null;
        }
    }

    /// <summary>
    /// Drops sets past the staleness bound, and — if that was not enough — the oldest entries, so the
    /// cache stays bounded by <see cref="MaxCachedJwks"/> rather than by the number of client records an
    /// anonymous DCR caller decided to create.
    /// </summary>
    private static void EvictExpiredJwks()
    {
        if (JwksCache.Count < MaxCachedJwks) return;

        var now = DateTimeOffset.UtcNow;
        foreach (var (url, entry) in JwksCache)
        {
            if (now - entry.FetchedAt >= MaxJwksStaleness)
                JwksCache.TryRemove(url, out _);
        }

        // Still full: the entries are all live, so evict by age. Re-fetching an evicted set costs one
        // request; keeping every set an attacker asked for costs the process.
        foreach (var (url, _) in JwksCache.OrderBy(e => e.Value.FetchedAt).Take(JwksCache.Count - MaxCachedJwks + 1))
            JwksCache.TryRemove(url, out _);
    }
}
