using System.Net;
using Microsoft.AspNetCore.Http;

namespace Authagonal.Protocol.Services;

/// <summary>
/// The one definition of "source" that every per-source quota in this product keys on.
/// </summary>
/// <remarks>
/// This lived in <c>Authagonal.Server.Services.Cluster.InternalEndpointGuard</c>, whose own remarks say
/// "every source-keyed limiter must come through here … there is now one function and no alternatives" —
/// after three sibling limiters had each read a peer address their own way and each been wrong differently.
/// It moved here because <c>Authagonal.Protocol</c> cannot reference the Server assembly, and the
/// client-secret throttle in <see cref="Endpoints.ClientAuthentication"/> needs the same key: the
/// alternative was a second copy in Protocol, which is precisely the shape that produced those three
/// divergences. <c>InternalEndpointGuard</c> now delegates here, so there is still exactly one function.
/// <para>
/// <b>Why the forwarded address is only used when the operator declared the proxy.</b> With no declaration,
/// the rightmost forwarded value may have been written by the caller, so keying on it lets anyone mint an
/// unlimited number of buckets and step around the quota entirely. With a declaration,
/// <c>UseForwardedHeaders</c> has already replaced <c>RemoteIpAddress</c> with a value the operator vouches
/// for, and the quota is correctly per-client.
/// </para>
/// <para>
/// <b>What the undeclared case costs.</b> Behind an L7 proxy with nothing declared, the raw peer is that
/// proxy for every request, so every caller shares one bucket and any one of them can spend the whole
/// budget. That is a real availability defect, and folding the forwarded value back in would reopen the
/// bypass above exactly — nothing observable distinguishes the two cases, which is what the operator's
/// declaration states and the server cannot otherwise know. The resolution is configuration:
/// <c>UseAuthagonal</c> warns at startup and a declared proxy makes the quota per-client. Do not "fix" this
/// by re-keying; fix the deployment.
/// </para>
/// </remarks>
public static class SourceQuota
{
    /// <summary>
    /// <see cref="HttpContext.Items"/> key holding the untampered peer address, stashed by middleware
    /// running ahead of <c>UseForwardedHeaders</c>. Absent means that middleware was not registered, which
    /// is treated as untrusted.
    /// </summary>
    public const string RawPeerAddressItem = "Authagonal.RawPeerAddress";

    /// <summary>
    /// <see cref="HttpContext.Items"/> key recording whether the operator DECLARED the proxy in front of
    /// this process (<c>ForwardedHeaders:KnownProxies</c> / <c>:KnownNetworks</c>). Absent means no.
    /// </summary>
    public const string ProxyTrustDeclaredItem = "Authagonal.ProxyTrustDeclared";

    /// <summary>The source dimension for a per-source quota bucket.</summary>
    public static string Key(HttpContext httpContext)
    {
        if (httpContext.Items.TryGetValue(ProxyTrustDeclaredItem, out var declared) && declared is true)
            return httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        // Undeclared: fall back to the peer we actually observed. See the remarks — this is the shared
        // bucket, and declaring the proxy is what resolves it.
        return RawPeerAddress(httpContext)?.ToString() ?? "unknown";
    }

    /// <summary>
    /// The peer address as observed by the server, before <c>UseForwardedHeaders</c> could replace it.
    /// </summary>
    /// <remarks>
    /// Falls back to the live connection address only when nothing was stashed AND no forwarded header is
    /// present, so a spoofed header can never be mistaken for a real peer.
    /// </remarks>
    public static IPAddress? RawPeerAddress(HttpContext httpContext)
    {
        // The PRESENCE of the key is what says the middleware ran, not the value. A server that reports no
        // peer at all (TestServer, some in-memory transports) stashes null, and that null is the honest
        // answer — falling through to Connection.RemoteIpAddress there would return the value
        // UseForwardedHeaders had just written from the client's own header.
        if (httpContext.Items.TryGetValue(RawPeerAddressItem, out var stashed))
            return stashed as IPAddress;

        // Not stashed. If the request carries a forwarded header we cannot tell whether RemoteIpAddress is
        // genuine or rewritten, so refuse to guess.
        //
        // Weaker than it looks: ForwardedHeadersMiddleware CONSUMES the header it honours, so by the time an
        // endpoint runs there may be none left to find. It catches the case where the header was never
        // trusted (and so survives) and nothing more — which is why the stash above, taken ahead of that
        // middleware, is the mechanism and this is only the backstop.
        if (httpContext.Request.Headers.ContainsKey("X-Forwarded-For")
            || httpContext.Request.Headers.ContainsKey("Forwarded"))
            return null;

        return httpContext.Connection.RemoteIpAddress;
    }
}
