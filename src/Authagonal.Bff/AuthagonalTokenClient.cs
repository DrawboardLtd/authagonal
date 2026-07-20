using System.Text.Json;

namespace Authagonal.Bff;

/// <summary>Default <see cref="ITokenClient"/>. Uses <c>client_secret_post</c> client authentication and
/// parses responses with <c>JsonDocument</c> (trim-safe). All endpoints are discovered per the tenant's
/// authority, and the tenant's confidential client credentials authenticate the call.</summary>
internal sealed class AuthagonalTokenClient(
    IHttpClientFactory httpClientFactory,
    BffOidcConfig oidc) : ITokenClient
{
    public async Task<TokenResult> ExchangeCodeAsync(BffTenantConfig tenant, string code, string redirectUri, string codeVerifier, CancellationToken ct = default)
    {
        var config = await oidc.GetAsync(tenant.Authority, ct);
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = codeVerifier,
            ["client_id"] = tenant.ClientId,
            ["client_secret"] = tenant.ClientSecret,
        };
        return await PostTokenAsync(config.TokenEndpoint, form, ct);
    }

    public async Task<TokenResult> RefreshAsync(BffTenantConfig tenant, string refreshToken, CancellationToken ct = default)
    {
        var config = await oidc.GetAsync(tenant.Authority, ct);
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = tenant.ClientId,
            ["client_secret"] = tenant.ClientSecret,
        };
        return await PostTokenAsync(config.TokenEndpoint, form, ct);
    }

    public async Task RevokeAsync(BffTenantConfig tenant, string refreshToken, CancellationToken ct = default)
    {
        var config = await oidc.GetAsync(tenant.Authority, ct);
        if (!config.AdditionalData.TryGetValue("revocation_endpoint", out var raw) || raw?.ToString() is not { } revocationEndpoint)
            return; // provider advertises no revocation endpoint

        var form = new Dictionary<string, string>
        {
            ["token"] = refreshToken,
            ["token_type_hint"] = "refresh_token",
            ["client_id"] = tenant.ClientId,
            ["client_secret"] = tenant.ClientSecret,
        };
        var client = httpClientFactory.CreateClient("AuthagonalBff");
        using var _ = await client.PostAsync(revocationEndpoint, new FormUrlEncodedContent(form), ct);
        // Best-effort: revocation failure must not block logout.
    }

    private async Task<TokenResult> PostTokenAsync(string tokenEndpoint, Dictionary<string, string> form, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient("AuthagonalBff");
        using var resp = await client.PostAsync(tokenEndpoint, new FormUrlEncodedContent(form), ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new BffTokenException($"Token endpoint returned {(int)resp.StatusCode}: {body}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var accessToken = root.TryGetProperty("access_token", out var at) ? at.GetString() : null;
        if (accessToken is null)
            throw new BffTokenException("Token response did not contain an access_token.");

        var refreshToken = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
        var idToken = root.TryGetProperty("id_token", out var it) ? it.GetString() : null;
        var expiresIn = root.TryGetProperty("expires_in", out var ei) && ei.TryGetInt32(out var e) ? e : 3600;

        return new TokenResult(accessToken, refreshToken, idToken, expiresIn);
    }
}
