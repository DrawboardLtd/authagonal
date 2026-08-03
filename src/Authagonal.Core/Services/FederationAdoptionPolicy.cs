using Authagonal.Core.Models;

namespace Authagonal.Core.Services;

/// <summary>
/// What happens when a federated login finds a pre-existing local account by email — decided once, for both
/// the OIDC and SAML hosts.
/// </summary>
/// <remarks>
/// Adoption is the step that turns a squatted account into a takeover, and the previous gates stopped the
/// wrong connection. Both hosts refused adoption when <c>ISsoDomainStore</c> routed the domain to a DIFFERENT
/// connection — which is a real check, but it never fires in the attack it was written for:
/// <list type="number">
/// <item>The attacker configures a connection they operate, with an empty <c>AllowedDomains</c> so nothing is
/// restricted, and JIT on.</item>
/// <item>They federate once asserting <c>ceo@acme.com</c> with <c>email_verified: true</c> — a claim chosen by
/// the party operating the upstream, which here is the attacker. The unverified-email gate passes. The
/// domain-routing gate passes too, because Acme has not onboarded and the domain has no row yet. An account
/// is minted bearing that address, with <c>EmailConfirmed = true</c> and the attacker's
/// <c>(provider, subject)</c> login attached.</item>
/// <item>Acme onboards. Their connection is the authority for <c>acme.com</c>, so when the genuine user first
/// signs in, every gate agrees: the domain IS theirs, the account matches by email, and it is adopted —
/// together with the squatter's still-valid login binding. The attacker signs in through their own connection
/// and is the CEO.</item>
/// </list>
/// So the missing question was never "does this connection own the domain" but "who else can already sign in
/// to this account". The routing check answers the first and cannot see the second.
/// <para>
/// A connection that IS the established authority for the domain therefore evicts foreign federation
/// bindings rather than inheriting them: between a squatter and the admin-vouched IdP for the domain, the IdP
/// wins. Refusing instead would have handed the attacker a permanent denial of service over any address they
/// squatted first, which is why eviction is the outcome and not an error.
/// </para>
/// </remarks>
public static class FederationAdoptionPolicy
{
    /// <summary>Provider-name prefixes for connection-scoped federation logins.</summary>
    /// <remarks>
    /// The prefix is what makes a binding attributable to a connection an administrator configured, and
    /// therefore to a squatter. Social providers (<c>google</c>, <c>github</c>, …) are deliberately NOT in
    /// this set: they are not connection-scoped, nobody can make Google assert an address they do not control,
    /// and evicting a user's own social login because their employer later onboarded SSO would be a
    /// self-inflicted lockout.
    /// </remarks>
    public static readonly string[] ConnectionScopedPrefixes = ["oidc:", "saml:"];

    public enum Decision
    {
        /// <summary>Nobody else can sign in to this account. Adopt it as before.</summary>
        Adopt,

        /// <summary>
        /// Another connection can sign in to this account, and this connection is the established authority
        /// for the email's domain. Remove those bindings, then adopt.
        /// </summary>
        EvictForeignBindingsThenAdopt,

        /// <summary>
        /// Another connection can sign in to this account and this one is not the domain's authority, so it
        /// does not get to decide who loses a binding.
        /// </summary>
        Refuse,
    }

    /// <summary>
    /// The federation logins on this account that belong to a DIFFERENT connection.
    /// </summary>
    /// <param name="logins">Every external login on the account being adopted.</param>
    /// <param name="thisProvider">This connection's provider name, e.g. <c>oidc:{connectionId}</c>.</param>
    public static List<ExternalLoginInfo> ForeignBindings(
        IEnumerable<ExternalLoginInfo> logins, string thisProvider)
    {
        var foreign = new List<ExternalLoginInfo>();
        foreach (var login in logins)
        {
            if (string.Equals(login.Provider, thisProvider, StringComparison.Ordinal)) continue;

            foreach (var prefix in ConnectionScopedPrefixes)
            {
                if (login.Provider.StartsWith(prefix, StringComparison.Ordinal))
                {
                    foreign.Add(login);
                    break;
                }
            }
        }
        return foreign;
    }

    /// <param name="connectionIsDomainAuthority">
    /// True when this connection is the ESTABLISHED authority for the email's domain — the routing table names
    /// it, or an administrator listed the domain in the connection's <c>AllowedDomains</c>. Both are
    /// first-party configuration. An upstream-supplied claim such as <c>email_verified</c> must never be
    /// passed here: that is the boolean the attacker sets.
    /// </param>
    /// <param name="foreignBindingCount">How many other connections can already sign in to the account.</param>
    public static Decision Evaluate(bool connectionIsDomainAuthority, int foreignBindingCount)
    {
        if (foreignBindingCount == 0) return Decision.Adopt;
        return connectionIsDomainAuthority
            ? Decision.EvictForeignBindingsThenAdopt
            : Decision.Refuse;
    }
}
