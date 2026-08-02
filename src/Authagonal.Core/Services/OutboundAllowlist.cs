using System.Net;
using System.Net.Sockets;

namespace Authagonal.Core.Services;

/// <summary>
/// Internal destinations an OPERATOR has explicitly permitted this server to originate requests to.
/// </summary>
/// <remarks>
/// <see cref="OutboundUrl"/> and <see cref="SafeOutboundConnect"/> refuse every internal target, and for a
/// target an attacker names that is the whole point. But not every outbound target is named by an attacker.
/// The distinction that matters is WHO SUPPLIED THE URL:
/// <list type="bullet">
/// <item>
/// A <c>jwks_uri</c>, a DCR-registered back-channel logout URI, a provisioning callback — chosen by a
/// registrant or a client. Naming an internal host there IS the attack, so those paths get
/// <see cref="None"/> and there is no way to widen them.
/// </item>
/// <item>
/// An upstream IdP's SAML metadata or OIDC discovery URL, a BFF upstream — chosen by the operator, in
/// configuration. Naming an internal host there is frequently the DEPLOYMENT: federating with an
/// on-premises IdP reachable only over a private network is a first-class configuration for an auth
/// product, and refusing it at the socket with no way to say otherwise makes the guard the outage.
/// </item>
/// </list>
/// <para>
/// So this is the operator's answer to "which internal destinations are mine". It is deliberately a
/// per-destination allowlist and not a global switch: widening one federation target must not also
/// re-open the cloud metadata service to an anonymous <c>/connect/token</c> request.
/// </para>
/// <para>
/// Entries take three forms, because the two layers of the guard see different things — the URL check sees
/// a NAME and the socket check sees an ADDRESS, and one deployment may need both waived:
/// </para>
/// <list type="bullet">
/// <item><c>idp.corp.internal</c> — that exact host, and every address it resolves to.</item>
/// <item><c>*.corp.internal</c> — any host under that suffix, and every address those resolve to.</item>
/// <item><c>10.4.0.0/16</c> or <c>10.4.1.7</c> — that network or address, whatever name reached it.</item>
/// </list>
/// <para>
/// A host entry permits the addresses its name resolves to precisely because the operator named the host:
/// the rebinding defence exists to stop an attacker choosing this server's destination, and here the
/// operator already chose it. An address entry is the other half — it permits a network without vouching
/// for any particular name, which is what a proxied or multi-homed internal service needs.
/// </para>
/// </remarks>
public sealed class OutboundAllowlist
{
    /// <summary>Permits nothing. The correct value on every registrant-supplied target.</summary>
    public static readonly OutboundAllowlist None = new([]);

    private readonly HashSet<string> _hosts;
    private readonly string[] _suffixes;
    private readonly (IPAddress Network, int PrefixLength)[] _networks;

    /// <param name="entries">
    /// Host names, <c>*.suffix</c> wildcards, addresses, or CIDR networks. Null and blank entries are
    /// ignored so an operator can leave placeholder lines in configuration.
    /// </param>
    /// <exception cref="ArgumentException">
    /// An entry contains <c>/</c> but is not a parseable CIDR network. Thrown rather than ignored: a
    /// mistyped CIDR that fell through to the host-name branch would silently permit nothing while the
    /// operator believed a network was open, and the symptom would be a refused connection with no
    /// mention of the typo that caused it.
    /// </exception>
    public OutboundAllowlist(IEnumerable<string?> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        _hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var suffixes = new List<string>();
        var networks = new List<(IPAddress, int)>();

        foreach (var raw in entries)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            // Trailing dots stripped here for the same reason OutboundUrl strips them from the host it is
            // handed: "idp.corp.internal." and "idp.corp.internal" are one destination to DNS, and an
            // allowlist that distinguished them would miss whichever form the configured URL happened
            // not to use.
            var entry = raw.Trim().TrimEnd('.');
            if (entry.Length == 0) continue;

            if (entry.Contains('/', StringComparison.Ordinal))
            {
                networks.Add(ParseNetwork(entry));
                continue;
            }

            if (IPAddress.TryParse(entry, out var literal))
            {
                networks.Add((Normalize(literal), Normalize(literal).GetAddressBytes().Length * 8));
                continue;
            }

            if (entry.StartsWith("*.", StringComparison.Ordinal))
            {
                var suffix = entry[1..]; // keep the leading dot: "*.corp.internal" → ".corp.internal"
                if (suffix.Length > 1) suffixes.Add(suffix);
                continue;
            }

            _hosts.Add(entry);
        }

        _suffixes = [.. suffixes];
        _networks = [.. networks];
    }

    /// <summary>True when this allowlist permits nothing, i.e. the guard is at full strength.</summary>
    public bool IsEmpty => _hosts.Count == 0 && _suffixes.Length == 0 && _networks.Length == 0;

    /// <summary>
    /// True when <paramref name="host"/> is a host the operator named — so the URL check may waive its
    /// internal-name rules for it, and the socket check may accept whatever it resolves to.
    /// </summary>
    public bool PermitsHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;

        var candidate = host.Trim().TrimEnd('.');
        if (candidate.Length == 0) return false;

        if (_hosts.Contains(candidate)) return true;

        foreach (var suffix in _suffixes)
        {
            if (candidate.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return true;
        }

        // A literal address in the URL is matched against the address entries, not the name entries —
        // otherwise `http://10.4.1.7/` would be refused by a list that says 10.4.0.0/16 is permitted.
        return IPAddress.TryParse(candidate, out var literal) && PermitsAddress(literal);
    }

    /// <summary>
    /// True when <paramref name="ip"/> falls in a network the operator named.
    /// </summary>
    /// <remarks>
    /// Name entries are deliberately NOT consulted here: this is asked at the socket, where all that is
    /// known about an address is the address. The caller checks the name separately, because it is the
    /// caller that knows which name the connection was opened for.
    /// </remarks>
    public bool PermitsAddress(IPAddress? ip)
    {
        if (ip is null || _networks.Length == 0) return false;

        var candidate = Normalize(ip);
        foreach (var (network, prefixLength) in _networks)
        {
            if (candidate.AddressFamily != network.AddressFamily) continue;
            if (IsInNetwork(candidate, network, prefixLength)) return true;
        }

        return false;
    }

    /// <summary>
    /// IPv4-mapped IPv6 collapsed to IPv4, matching what <see cref="OutboundUrl"/> does before it judges
    /// an address — otherwise <c>::ffff:10.4.1.7</c> would be blocked by one and unmatched by the other.
    /// </summary>
    private static IPAddress Normalize(IPAddress ip) => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;

    private static (IPAddress Network, int PrefixLength) ParseNetwork(string entry)
    {
        var slash = entry.IndexOf('/', StringComparison.Ordinal);
        var addressPart = entry[..slash];
        var prefixPart = entry[(slash + 1)..];

        if (!IPAddress.TryParse(addressPart, out var address)
            || !int.TryParse(prefixPart, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var prefixLength))
        {
            throw new ArgumentException(
                $"'{entry}' is not a valid CIDR network. Use an address and a prefix length, " +
                "e.g. 10.4.0.0/16 or fd00:1234::/48.", nameof(entry));
        }

        address = Normalize(address);
        var maxPrefix = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        if (prefixLength < 0 || prefixLength > maxPrefix)
        {
            throw new ArgumentException(
                $"'{entry}' has a prefix length outside 0-{maxPrefix} for its address family.",
                nameof(entry));
        }

        return (address, prefixLength);
    }

    private static bool IsInNetwork(IPAddress candidate, IPAddress network, int prefixLength)
    {
        var candidateBytes = candidate.GetAddressBytes();
        var networkBytes = network.GetAddressBytes();
        if (candidateBytes.Length != networkBytes.Length) return false;

        var wholeBytes = prefixLength / 8;
        for (var i = 0; i < wholeBytes; i++)
        {
            if (candidateBytes[i] != networkBytes[i]) return false;
        }

        var remainingBits = prefixLength % 8;
        if (remainingBits == 0) return true;

        var mask = (byte)(0xFF << (8 - remainingBits));
        return (candidateBytes[wholeBytes] & mask) == (networkBytes[wholeBytes] & mask);
    }
}
