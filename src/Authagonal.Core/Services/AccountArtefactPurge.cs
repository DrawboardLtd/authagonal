using Authagonal.Core.Stores;

namespace Authagonal.Core.Services;

/// <summary>
/// Everything keyed on a user id that must not outlive the account — removed by one rule, for every delete
/// path.
/// </summary>
/// <remarks>
/// <c>IMfaStore.DeleteAllCredentialsAsync</c> had exactly one caller in the whole product: the admin MFA-reset
/// endpoint. Neither <c>DELETE /api/v1/profile/{userId}</c> nor SCIM <c>DELETE /scim/v2/Users/{id}</c> touched
/// the MFA store, so a deleted account's TOTP secret, recovery-code hashes, WebAuthn public keys and WebAuthn
/// credential-id index rows all survived, keyed on the user id. SCIM group membership survived too:
/// <c>DeleteUserAsync</c> never removed the departing user from any group, and
/// <c>RetainOwnedMembersAsync</c> deliberately KEEPS tombstoned members because they still carry the owning
/// client id.
/// <para>
/// That mattered because the id comes back. The SCIM create path reclaims a tombstoned row, and the admin
/// create endpoint accepts a caller-supplied <c>UserId</c>. The reclaim asserted in a comment that "nothing the
/// deleted account held survives" — false for the second factor, and the passwordless sign-in path never
/// consults <c>MfaEnabled</c>: it resolves the account from the credential. So an attacker who had enrolled a
/// passkey kept a way in across the exact remedy an incident responder reaches for — delete the account and
/// re-create it — and needed no password, no email and no session.
/// </para>
/// <para>
/// The membership half needs no attacker at all. An offboarded address re-issued months later to a different
/// person inherited every group the departed employee still occupied, including role-mapped ones, so the new
/// user silently received roles no administrator granted and the IdP held no record of the assignment.
/// </para>
/// </remarks>
public static class AccountArtefactPurge
{
    /// <summary>
    /// Removes the second-factor credentials and SCIM group memberships belonging to
    /// <paramref name="userId"/>.
    /// </summary>
    /// <remarks>
    /// Both stores are optional so a host that registers neither still deletes users. Failures are NOT
    /// swallowed here: a delete that silently leaves a passkey behind is the defect, and the caller decides
    /// whether to fail the request or log — see each call site.
    /// </remarks>
    public static async Task PurgeAsync(
        string userId,
        IMfaStore? mfaStore,
        IScimGroupStore? groupStore,
        CancellationToken ct = default)
    {
        if (mfaStore is not null)
            await mfaStore.DeleteAllCredentialsAsync(userId, ct).ConfigureAwait(false);

        if (groupStore is null) return;

        foreach (var group in await groupStore.GetGroupsByUserIdAsync(userId, ct).ConfigureAwait(false))
        {
            if (!group.MemberUserIds.Remove(userId)) continue;
            await groupStore.UpdateAsync(group, ct).ConfigureAwait(false);
        }
    }
}
