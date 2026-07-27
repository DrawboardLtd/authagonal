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

    // Config-derived public key material only — identical on every pod, safe to cache in-memory.
    private static readonly ConcurrentDictionary<string, (JsonWebKeySet Keys, DateTimeOffset FetchedAt)> JwksCache = new();
    private static readonly HttpClient FallbackHttpClient = new() { Timeout = TimeSpan.FromSeconds(10) };
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
    public static async Task<(OAuthClient? Client, IResult? Error)> AuthenticateAsync(
        HttpContext httpContext,
        IFormCollection form,
        IClientStore clientStore,
        IClientSecretVerifier secretVerifier,
        Func<string, string, IResult> error,
        CancellationToken ct)
    {
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

        if (client.RequireClientSecret)
        {
            if (string.IsNullOrWhiteSpace(clientSecret))
                return (null, error("invalid_client", "client_secret is required"));

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
            // Pin the algorithms rather than accepting whatever the assertion header asks for. Keys come
            // from the client's registered JWKS and never from a jku/jwk/x5u header (IdentityModel does
            // not resolve those), so this is not the classic RS/HS confusion — but an explicit list means
            // a symmetric key appearing in a client's JWKS cannot quietly turn client authentication into
            // an HMAC over a value the client also publishes. RFC 7518 asymmetric algorithms only.
            ValidAlgorithms =
            [
                SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSha384, SecurityAlgorithms.RsaSha512,
                SecurityAlgorithms.RsaSsaPssSha256, SecurityAlgorithms.RsaSsaPssSha384, SecurityAlgorithms.RsaSsaPssSha512,
                SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.EcdsaSha384, SecurityAlgorithms.EcdsaSha512,
            ],
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

        // Replay: each assertion is single-use for its lifetime. Optional store — a
        // Protocol-only host without one still gets signature/audience/lifetime enforcement.
        var replayCache = httpContext.RequestServices.GetService<IRevokedTokenStore>();
        if (replayCache is not null)
        {
            var replayKey = $"client_assertion:{clientId}:{jti}";
            if (await replayCache.IsRevokedAsync(replayKey, ct))
                return (null, error("invalid_client", "client_assertion has already been used"));
            await replayCache.AddAsync(replayKey, expiresAt, clientId, ct);
        }

        return (client, null);
    }

    /// <summary>Inline JWKS wins; a JwksUri is fetched through the host's HttpClient factory
    /// (or a shared fallback) and cached for <see cref="JwksCacheTtl"/>.</summary>
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

        if (string.IsNullOrWhiteSpace(client.JwksUri) ||
            !Uri.TryCreate(client.JwksUri, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
            return null;

        if (JwksCache.TryGetValue(client.JwksUri, out var cached) &&
            DateTimeOffset.UtcNow - cached.FetchedAt < JwksCacheTtl)
            return cached.Keys;

        try
        {
            var http = httpContext.RequestServices.GetService<IHttpClientFactory>()?.CreateClient("AuthagonalJwks")
                ?? FallbackHttpClient;
            var json = await http.GetStringAsync(uri, ct);
            var keys = new JsonWebKeySet(json);
            JwksCache[client.JwksUri] = (keys, DateTimeOffset.UtcNow);
            return keys;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or ArgumentException)
        {
            // A stale cached set beats an outage-shaped auth failure.
            return JwksCache.TryGetValue(client.JwksUri, out var stale) ? stale.Keys : null;
        }
    }
}
