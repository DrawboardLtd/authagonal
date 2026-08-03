using System.Security.Cryptography;
using System.Text;

namespace Authagonal.Core.Services;

/// <summary>
/// Rewrites an <see cref="ISecretProvider"/> name into one a backing store will accept, without ever
/// mapping two distinct names onto the same one.
/// </summary>
/// <remarks>
/// <see cref="ISecretProvider.ProtectAsync"/>'s contract is explicit that the name is the storage key and
/// MUST be unique per distinct value, because reusing one silently overwrites the earlier secret and every
/// reference then resolves to whichever was written last. Both bundled providers broke that in three ways at
/// once: every disallowed character was mapped to <c>'-'</c> (so <c>"a b"</c> and <c>"a-b"</c> collide), the
/// input was truncated to the length budget <b>before</b> sanitising (so two long names sharing a prefix
/// collide), and the result was <c>Trim('-')</c>'d (so <c>"-x"</c> and <c>"x"</c> collide).
/// <para>
/// That is reachable, not theoretical. Secret names are built from the user id —
/// <c>mfa-totp-{userId}</c>, <c>mfa-recovery-{userId}-{credentialId}</c> — and a federated connection with
/// <c>UseUpstreamSubjectAsUserId</c> takes the user id straight from the upstream <c>sub</c>
/// (OidcEndpoints.cs:542), which is whatever the external IdP puts there: an email, a scoped id with
/// punctuation, something arbitrarily long. Two such users whose subs differ only in a character that folds
/// to <c>'-'</c> would share one stored TOTP seed, and the later enrolment would silently overwrite the
/// earlier one. This is the same class as the 0.20.0 recovery-code defect, where one name was reused across
/// ten codes and "nine of every ten were dead, and the tenth worked ten times".
/// </para>
/// <para>
/// So: whenever the rewrite loses information for any of those three reasons, a short hash of the ORIGINAL
/// name is appended, with room reserved for it inside the budget. Distinct inputs therefore cannot fold onto
/// one key, and a name that needed no rewriting is left exactly as it was — so existing stored secrets keep
/// resolving.
/// </para>
/// </remarks>
public static class SecretNameSanitizer
{
    /// <summary>
    /// Hex characters of SHA-256 appended when the rewrite is lossy. 12 hex characters is 48 bits: with
    /// the number of secrets any single deployment holds, an accidental collision is not a live concern,
    /// and the suffix has to be short enough to leave a readable name in front of it.
    /// </summary>
    private const int HashChars = 12;

    /// <summary>
    /// A store-acceptable name for <paramref name="name"/>.
    /// </summary>
    /// <param name="name">The caller's storage key. Never truncated before hashing.</param>
    /// <param name="maxLength">The store's name-length limit (127 for Key Vault, 512 for Secrets Manager).</param>
    /// <param name="extraAllowed">
    /// Characters the store permits in addition to ASCII letters and digits — <c>"/_+=.@-"</c> for Secrets
    /// Manager, <c>"-"</c> for Key Vault. Anything else maps to <c>'-'</c>.
    /// </param>
    public static string Sanitize(string name, int maxLength, string extraAllowed)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, HashChars + 1);

        // Mapped over the WHOLE input. Truncating first is what let two long names with a common prefix
        // become one key.
        var mapped = new char[name.Length];
        var folded = false;
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            var ok = char.IsAsciiLetterOrDigit(c) || extraAllowed.Contains(c, StringComparison.Ordinal);
            mapped[i] = ok ? c : '-';
            folded |= !ok;
        }

        var body = new string(mapped).Trim('-');

        // Any of the three lossy rewrites means the result no longer identifies the input on its own.
        var lossy = folded || body.Length != name.Length || name.Length > maxLength;
        if (!lossy)
            return body;

        var suffix = Hash(name);

        // Reserve room for "-" + hash. A body that vanished entirely (a name of nothing but disallowed
        // characters) leaves the hash alone, which is still a valid name and still unique to the input.
        var room = maxLength - suffix.Length - 1;
        if (room <= 0 || body.Length == 0)
            return suffix;

        return $"{body[..Math.Min(body.Length, room)].TrimEnd('-')}-{suffix}";
    }

    private static string Hash(string name)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..HashChars];
}
