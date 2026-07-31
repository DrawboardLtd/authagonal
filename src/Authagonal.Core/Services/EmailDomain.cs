namespace Authagonal.Core.Services;

/// <summary>
/// The one place an email address is split into local part and domain.
/// </summary>
/// <remarks>
/// There were two conventions in the tree. The storage layer and the auto-confirm check took
/// everything after the LAST '@' (matching <c>UserEmailDomainEntity.DomainOf</c>, which is what the
/// domain blind index answers "all users @acme.com" from). Every forced-SSO gate — password login,
/// passkey login, passkey enrolment, and the authorize login_hint — took everything after the FIRST.
/// <para>
/// For an address with two '@' the two disagree, and the SSO gate is the one that fails open:
/// <c>bob@x@corp.com</c> yields <c>x@corp.com</c> there, which matches no registered SSO domain, so
/// forced SSO simply does not fire — while the rest of the system files the account under
/// <c>corp.com</c>. Registration accepted such addresses (it required only that '@' appear somewhere)
/// and SCIM create did not validate the shape at all. Those gates are load-bearing: the passkey one
/// carries a comment saying a local passkey "must NOT sidestep it (and its 2FA / conditional access /
/// deprovisioning)".
/// </para>
/// </remarks>
public static class EmailDomain
{
    /// <summary>
    /// The lowercased domain of <paramref name="email"/>, or null when it carries none.
    /// </summary>
    public static string? Of(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;

        var at = email.LastIndexOf('@');
        if (at < 0 || at == email.Length - 1) return null;

        return email[(at + 1)..].ToLowerInvariant();
    }

    /// <summary>
    /// True when <paramref name="email"/> has exactly one '@' with a non-empty local part and domain.
    /// </summary>
    /// <remarks>
    /// An address with two or more '@' has a domain that is ambiguous by construction, which is what
    /// let one part of the system read it as a different domain from another. Rejecting it at every
    /// creation path removes the ambiguity rather than trying to agree on how to resolve it.
    /// </remarks>
    public static bool HasUnambiguousDomain(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return false;

        var at = email.IndexOf('@');
        return at > 0 && at == email.LastIndexOf('@') && at < email.Length - 1;
    }
}
