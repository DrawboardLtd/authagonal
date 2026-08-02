using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace Authagonal.Core.Services;

/// <summary>
/// The SSRF guard at the point it cannot be evaded: the socket.
/// </summary>
/// <remarks>
/// <see cref="OutboundUrl.IsSafe"/> inspects a URL, and a URL only says what it says. Where the host is
/// a literal address that is enough; where it is a NAME it is not, because the name is resolved later, by
/// something else, and whoever owns the name decides what it resolves to. <c>logout.attacker.test</c>
/// passes every textual check and answers with <c>169.254.169.254</c>.
///
/// <para>
/// Resolving at the moment the URL is stored does not fix it either, in two directions at once. An
/// attacker can answer truthfully at registration and differently at delivery. And pinning what was
/// resolved then goes stale: relying-party addresses move for entirely ordinary reasons — failover,
/// autoscaling, a CDN, a cloud provider reclaiming the address — so a pin taken in one month can point at
/// a stranger's host in the next, and this server would be POSTing logout tokens carrying <c>sub</c> and
/// <c>sid</c> to whoever inherited it.
/// </para>
///
/// <para>
/// So the check belongs here, on every connection: resolve, refuse every address that is internal, and
/// connect to an address that was actually checked. Pinning the socket to the validated address is the
/// part that closes the race — resolving, approving, and then handing the NAME to the socket would let a
/// second lookup return something else between the two (DNS rebinding). And because a redirect is a new
/// connection, this runs again on every hop for free, which is the property a one-off check at the top of
/// a request can never have.
/// </para>
///
/// <para>
/// This does not replace <see cref="OutboundUrl.IsSafe"/>. That one refuses a bad URL early, where the
/// error is attributable to the admin who typed it; this one refuses a bad ADDRESS late, where no lie
/// about DNS can help. Keep both.
/// </para>
///
/// <para>
/// Two things about WHERE this is attached, both of which were got wrong once and are easy to get wrong
/// again. It belongs on clients whose target is chosen by a REGISTRANT (a <c>jwks_uri</c>, a DCR-registered
/// back-channel logout URI, a provisioning callback), because there an internal host is the attack. On a
/// client whose target the OPERATOR configured, an internal host is frequently the deployment, and the
/// guard has to be given an <see cref="OutboundAllowlist"/> or it becomes the outage — see that type. And
/// wherever it IS attached, the handler must set <c>UseProxy = false</c>: with a proxy in effect
/// <c>SocketsHttpHandler</c> invokes the callback with the PROXY's endpoint and never the target's, so the
/// guard inspects the proxy, finds it perfectly routable, and waves everything through.
/// </para>
/// </remarks>
public static class SafeOutboundConnect
{
    /// <summary>Resolves a host name to addresses. Injectable so the guard is testable without DNS.</summary>
    public delegate Task<IPAddress[]> HostResolver(string host, CancellationToken ct);

    private static readonly HostResolver SystemResolver =
        (host, ct) => Dns.GetHostAddressesAsync(host, ct);

    /// <summary>
    /// A <c>SocketsHttpHandler.ConnectCallback</c> that refuses to open a connection to an internal
    /// address, whatever the name said.
    /// </summary>
    /// <param name="allowLoopback">
    /// Permit 127.0.0.0/8 and ::1. Off for everything the server initiates; the one caller that passes
    /// true is the front-channel logout path, where the fetch is made by the USER's browser rather than by
    /// this server and a local development relying party is a supported configuration.
    /// </param>
    /// <param name="resolver">Override for tests. Defaults to the system resolver.</param>
    /// <param name="allowlist">
    /// Internal destinations the operator configured this server to reach. Pass one only on a client whose
    /// target comes from operator configuration; leave it null on every registrant-supplied target, where
    /// there is deliberately no way to widen the guard. See <see cref="OutboundAllowlist"/>.
    /// </param>
    public static Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>> Callback(
        bool allowLoopback = false, HostResolver? resolver = null, OutboundAllowlist? allowlist = null)
    {
        var resolve = resolver ?? SystemResolver;

        return async (context, ct) => await ConnectAsync(
            context.DnsEndPoint.Host, context.DnsEndPoint.Port, allowLoopback, resolve, ct, allowlist)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Resolve, refuse anything internal, and connect to an address that was checked.
    /// </summary>
    /// <remarks>
    /// The callback is a two-line adapter over this, so a test can drive the real decision:
    /// <c>SocketsHttpConnectionContext</c> has no public constructor, and a test that reimplemented the
    /// logic in order to reach it would be asserting about a copy. Public for that reason and no other.
    /// </remarks>
    public static async Task<Stream> ConnectAsync(
        string host, int port, bool allowLoopback, HostResolver resolve, CancellationToken ct,
        OutboundAllowlist? allowlist = null)
    {
        {
            // A host the operator named is permitted whatever it resolves to, including the all-addresses
            // rule below. That is not a hole in the rebinding defence: the defence exists to stop an
            // ATTACKER choosing this server's destination, and here the operator already chose it by name.
            var operatorNamedHost = allowlist?.PermitsHost(host) == true;

            IPAddress[] candidates;
            if (IPAddress.TryParse(host, out var literal))
            {
                candidates = [literal];
            }
            else
            {
                try
                {
                    candidates = await resolve(host, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is SocketException or ArgumentException)
                {
                    throw new HttpRequestException($"Could not resolve '{host}'.", ex);
                }
            }

            // EVERY returned address must pass, not merely one of them. A name answering with both a
            // public address and an internal one is the rebinding attack expressed in a single response:
            // accepting it because one entry looked fine would let the attacker choose which the socket
            // gets, and the "choice" is whatever ordering the resolver happened to return.
            var allowed = operatorNamedHost
                ? candidates
                : candidates.Where(ip => OutboundUrl.IsAllowedAddress(ip, allowLoopback, allowlist)).ToArray();
            if (allowed.Length == 0 || allowed.Length != candidates.Length)
            {
                throw new HttpRequestException(
                    $"Refusing to connect to '{host}': it resolves to an address this server will not " +
                    "originate requests to (loopback, link-local, private, or otherwise internal). If this " +
                    "is an internal destination you deploy on purpose, list it in Auth:AllowedInternalTargets.");
            }

            // Connect to the ADDRESS that was checked, never back to the name — re-resolving here is the
            // rebinding window this whole callback exists to close.
            Exception? last = null;
            foreach (var address in allowed)
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true,
                };

                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, port), ct).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (Exception ex)
                {
                    socket.Dispose();
                    last = ex;
                    // Try the next validated address — a multi-homed host with one unreachable family is
                    // ordinary, and it is not a reason to fail the request.
                }
            }

            throw new HttpRequestException(
                $"Could not connect to '{host}' on port {port}.", last);
        }
    }
}
