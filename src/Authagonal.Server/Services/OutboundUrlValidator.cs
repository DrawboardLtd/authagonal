namespace Authagonal.Server.Services;

/// <summary>
/// Server-layer alias for <see cref="Authagonal.Core.Services.OutboundUrl"/>.
/// </summary>
/// <remarks>
/// The implementation moved to Core so <c>Authagonal.Protocol</c> can use it too — the client
/// <c>jwks_uri</c> fetch in <c>ClientAuthentication</c> lives there and had no SSRF check at all, which
/// meant an admin-registered (or DCR-registered) jwks_uri could point at an internal address and be fetched
/// from an anonymous <c>/connect/token</c> request. Kept as a shim so existing call sites and tests are
/// unchanged.
/// </remarks>
public static class OutboundUrlValidator
{
    public static bool IsSafe(string? url) => Authagonal.Core.Services.OutboundUrl.IsSafe(url);
}
