using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Authagonal.Core.Models;

namespace Authagonal.Core.Services;

/// <summary>
/// Computes <see cref="AuthUser.ConcurrencyToken"/> — a digest of the decision-carrying state of a user
/// record, which every <c>IUserStore.UpdateAsync</c> uses to refuse a write built from a stale read.
/// </summary>
/// <remarks>
/// <para>
/// Why a digest of the CONTENT rather than the backend's own row revision (an Azure ETag, a DynamoDB
/// <c>_v</c>, a SQL row version): the login stamps write the same row on every single sign-in, so any
/// revision counter moves constantly. Matching on one would make an administrative write fail whenever
/// the account happened to be logging in — and since the attacker in the reported scenario controls the
/// login rate, that turns a silent lost update into a denial of the very remediation the finding is
/// about. It would also break the guarantee <c>LoginStampConcurrencyTests</c> already pins: a SCIM
/// deprovision must succeed AND stick while a burst of logins runs.
/// </para>
/// <para>
/// So the token covers everything the store persists EXCEPT <see cref="AuthUser.LastLoginAt"/> and
/// <see cref="AuthUser.UpdatedAt"/> — the two columns a login stamp writes that decide nothing. A login
/// on a clean account leaves the digest identical and administrative writes proceed; a password reset,
/// a security-stamp rotation, a deactivation, a role revocation, a lockout — anything that actually
/// decides something — moves it, and a write that never saw the change is refused.
/// </para>
/// <para>
/// <see cref="AuthUser.AccessFailedCount"/> and <see cref="AuthUser.LockoutEnd"/> are deliberately IN:
/// a profile update built from a pre-lockout snapshot writes them back and clears an active lockout
/// mid-brute-force, which is the silent unlock the review called out.
/// </para>
/// </remarks>
public static class UserRevision
{
    private const char Separator = '\u001f';

    public static string Of(AuthUser user)
    {
        var sb = new StringBuilder();

        // Length-prefixed, so no combination of field values can be re-partitioned into a different
        // record with the same digest.
        void Add(string? value)
            => sb.Append(value?.Length.ToString(CultureInfo.InvariantCulture) ?? "-")
                 .Append(':').Append(value).Append(Separator);

        // Id is deliberately absent: the stores look a record up BY id, so no racing writer can change it.
        // The Azure store's env-prefix strip used to happen AFTER the model was built, which would have made
        // the same record digest differently depending on where in the read path it was taken; the strip now
        // lives inside UserEntity.ToModel, and Id staying out of the digest keeps that move invisible here.
        Add(user.Email);
        Add(user.NormalizedEmail);
        Add(user.PasswordHash);
        Add(user.PendingPasswordHash);
        Add(user.PendingClaimJson);
        Add(user.EmailConfirmed ? "1" : "0");
        Add(user.FirstName);
        Add(user.LastName);
        Add(user.CompanyName);
        Add(user.Phone);
        Add(user.Locale);
        Add(user.OrganizationId);
        Add(user.AccessFailedCount.ToString(CultureInfo.InvariantCulture));
        Add(user.LockoutEnabled ? "1" : "0");
        Add(Stamp(user.LockoutEnd));
        Add(user.SecurityStamp);
        Add(user.MfaEnabled ? "1" : "0");
        Add(user.ExternalId);
        Add(user.IsActive ? "1" : "0");
        Add(user.ScimProvisionedByClientId);
        Add(Stamp(user.ScimDeletedAt));
        Add(Stamp(user.CreatedAt));

        // Order-insensitive: a backend that round-trips these through JSON or a dictionary may reorder
        // them, and a reordering is not a change.
        foreach (var role in user.Roles.OrderBy(r => r, StringComparer.Ordinal)) Add(role);
        sb.Append(Separator);
        foreach (var (key, value) in user.CustomAttributes.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            Add(key);
            Add(value);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..32];
    }

    // Truncated to whole milliseconds: Azure Table Storage does not round-trip finer precision, and the
    // digest has to be identical for an instance held in memory and the same instance read back.
    private static string? Stamp(DateTimeOffset? value)
        => value is { } v ? Stamp(v) : null;

    private static string Stamp(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMillisecond, TimeSpan.Zero)
            .ToString("O", CultureInfo.InvariantCulture);
    }
}
