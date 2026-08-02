namespace Authagonal.Server.Services;

/// <summary>
/// Server-layer alias for <see cref="Authagonal.Core.Services.SafeOutboundHttp"/>.
/// </summary>
/// <remarks>
/// The implementation moved to Core alongside <see cref="OutboundUrlValidator"/>, so
/// <c>Authagonal.Protocol</c> can use it for the client <c>jwks_uri</c> fetch — the one server-initiated
/// outbound GET in the product that was still raw, on a redirect-following client, from an anonymous
/// <c>/connect/token</c> request. Kept as a shim so existing call sites and tests are unchanged.
/// </remarks>
public static class SafeOutboundHttp
{
    /// <inheritdoc cref="Authagonal.Core.Services.SafeOutboundHttp.MaxResponseBytes"/>
    public const int MaxResponseBytes = Authagonal.Core.Services.SafeOutboundHttp.MaxResponseBytes;

    /// <inheritdoc cref="Authagonal.Core.Services.SafeOutboundHttp.SendAsync"/>
    public static Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpRequestMessage request, ILogger? logger = null,
        CancellationToken ct = default, Authagonal.Core.Services.OutboundAllowlist? allowlist = null) =>
        Authagonal.Core.Services.SafeOutboundHttp.SendAsync(client, request, logger, ct, allowlist);

    /// <inheritdoc cref="Authagonal.Core.Services.SafeOutboundHttp.GetStringAsync"/>
    public static Task<string> GetStringAsync(
        HttpClient client, string url, ILogger? logger = null, CancellationToken ct = default,
        Authagonal.Core.Services.OutboundAllowlist? allowlist = null) =>
        Authagonal.Core.Services.SafeOutboundHttp.GetStringAsync(client, url, logger, ct, allowlist);
}
