using Authagonal.Core.Models;

namespace Authagonal.Core.Stores;

public interface IMfaStore
{
    // Credentials
    Task<IReadOnlyList<MfaCredential>> GetCredentialsAsync(string userId, CancellationToken ct = default);
    Task<MfaCredential?> GetCredentialAsync(string userId, string credentialId, CancellationToken ct = default);
    Task CreateCredentialAsync(MfaCredential credential, CancellationToken ct = default);
    Task UpdateCredentialAsync(MfaCredential credential, CancellationToken ct = default);
    Task DeleteCredentialAsync(string userId, string credentialId, CancellationToken ct = default);
    Task DeleteAllCredentialsAsync(string userId, CancellationToken ct = default);

    /// <summary>
    /// Claims a TOTP time step: advances the stored <c>LastTotpStep</c> to <paramref name="step"/>, but
    /// only if it is still below it. Returns false when it is not — meaning some other request already
    /// spent this code — and the caller MUST then reject the code.
    /// </summary>
    /// <remarks>
    /// There is deliberately no unconditional write here, for the same reason
    /// <see cref="TryStoreWebAuthnCredentialIdMappingAsync"/> has none: the claim has to BE the write.
    /// Verifying a code is otherwise read-check-write across three round trips, so N requests carrying
    /// the same captured 6-digit code all read the same pre-update step, all match, and all succeed.
    /// That is exactly the real-time-phishing replay RFC 6238 §5.2 requires a verifier to refuse — the
    /// attacker submits the victim's code alongside the victim's own login and both are let in, with
    /// nothing visibly wrong to the victim. A compare-and-set narrows the window from the full 30-second
    /// step to nothing at all.
    /// </remarks>
    Task<bool> TryClaimTotpStepAsync(string userId, string credentialId, long step, CancellationToken ct = default);

    /// <summary>
    /// Consumes a recovery code: flips <c>IsConsumed</c> from false to true. Returns false when it was
    /// already true, which means a concurrent request spent it first and this one must be rejected.
    /// </summary>
    /// <remarks>
    /// Same race as <see cref="TryClaimTotpStepAsync"/>, one step more valuable: a recovery code is a
    /// single-use bypass of the whole second factor, and two requests presenting the same one both saw
    /// <c>IsConsumed = false</c> and both blind-wrote it true.
    /// </remarks>
    Task<bool> TryConsumeRecoveryCodeAsync(string userId, string credentialId, CancellationToken ct = default);

    /// <summary>
    /// Rewrites a recovery credential's protected secret, but only while it is still unconsumed. Returns
    /// false when the credential is gone, already consumed, or was written by someone else first — in which
    /// case the rewrite is simply skipped.
    /// </summary>
    /// <remarks>
    /// Exists because the alternative was a blind full-row write, and that reopened
    /// <see cref="TryConsumeRecoveryCodeAsync"/> from inside the same handler. The recovery path upgrades
    /// every legacy unsalted-SHA-256 digest it touches, for the whole set, from a snapshot taken BEFORE
    /// verification. Persisting that snapshot with an unconditional upsert put <c>IsConsumed</c> back to
    /// false for any code a concurrent request had spent in the meantime — re-arming a single-use bypass of
    /// the entire second factor, which is precisely what the conditional consume above was added to prevent.
    /// <para>
    /// Deliberately not an unconditional "update the secret" either: the guard is the point. And unlike the
    /// claim operations this does NOT stamp <c>LastUsedAt</c> — the upgrade sweeps the user's whole set, so
    /// stamping would mark every recovery code as used because one of them was.
    /// </para>
    /// </remarks>
    Task<bool> TryUpgradeRecoverySecretAsync(
        string userId, string credentialId, string secretProtected, CancellationToken ct = default);

    /// <summary>
    /// Records a WebAuthn assertion: raises the stored sign counter to <paramref name="signCount"/> and
    /// stamps <c>LastUsedAt</c>, but only while the credential still exists and its counter has not already
    /// passed that value. Returns false otherwise, and the caller MUST then refuse the assertion.
    /// </summary>
    /// <remarks>
    /// Replaces a blind <see cref="UpdateCredentialAsync"/> of a snapshot read before the assertion was
    /// verified, which was wrong twice over. The counter is clone detection: it must only ever go up, and
    /// writing back a value captured before verification could move it DOWN past a concurrent assertion's
    /// higher one, re-arming the replay Fido2's own regression check exists to catch. And an unconditional
    /// upsert re-CREATES a row that <see cref="DeleteAllCredentialsAsync"/> removed in the meantime, so an
    /// administrator revoking a user's second factor mid-assertion had it resurrected — by the request they
    /// were revoking — and the handler then signed the session in.
    /// <para>
    /// The guard permits equality, because a sign counter of zero means "this authenticator does not
    /// implement one" (WebAuthn §6.1.1) and such an authenticator reports 0 forever. Requiring a strict
    /// increase would refuse every assertion from it. Monotonicity is what matters, not motion.
    /// </para>
    /// </remarks>
    Task<bool> TryRecordWebAuthnUseAsync(
        string userId, string credentialId, uint signCount, CancellationToken ct = default);

    /// <summary>
    /// Names a pending credential, completing its enrolment — but only while it still exists. Returns false
    /// when it is gone, and the caller MUST then abandon the enrolment rather than continue.
    /// </summary>
    /// <remarks>
    /// The TOTP confirm path claimed its time step conditionally (<see cref="TryClaimTotpStepAsync"/>) and
    /// then persisted a full-row snapshot read BEFORE that claim, with a comment asserting the write could
    /// not undo it. It could, in two ways. The snapshot's <c>LastTotpStep</c> is whatever was stored before
    /// the claim, so writing the row back could move the step behind a concurrent verification's — putting a
    /// spent code back in play for the rest of its window. And the upsert re-created a credential an
    /// administrator had revoked between the read and the write, after which the same handler set
    /// <c>MfaEnabled</c> and issued a session cookie.
    /// <para>
    /// So this writes the name and nothing else. The step is already claimed and already stamped by the
    /// claim; there is no second value to persist, which is the point — a conditional write that carries
    /// fields it does not own is a blind write wearing a guard.
    /// </para>
    /// </remarks>
    Task<bool> TryActivateCredentialAsync(
        string userId, string credentialId, string name, CancellationToken ct = default);

    // WebAuthn credential ID index
    Task<(string UserId, string CredentialId)?> FindByWebAuthnCredentialIdAsync(byte[] webAuthnCredentialId, CancellationToken ct = default);

    /// <summary>
    /// Claims the credential-id index row, returning false when it is already claimed. There is
    /// deliberately no unconditional write: this row is what makes a credential id resolve to exactly
    /// one account, so an upsert would let a second registration of the same credential id silently
    /// repoint the index — either at a second row belonging to the same user (whose sign counter starts
    /// over, weakening clone detection, and whose deletion takes the shared index row with it) or, on a
    /// race between two users, at the wrong account entirely. A read followed by a write cannot close
    /// that; the claim has to be the write.
    /// </summary>
    Task<bool> TryStoreWebAuthnCredentialIdMappingAsync(byte[] webAuthnCredentialId, string userId, string credentialId, CancellationToken ct = default);

    Task DeleteWebAuthnCredentialIdMappingAsync(byte[] webAuthnCredentialId, CancellationToken ct = default);

    // Challenges
    Task StoreChallengeAsync(MfaChallenge challenge, CancellationToken ct = default);
    Task<MfaChallenge?> GetChallengeAsync(string challengeId, CancellationToken ct = default);
    Task<MfaChallenge?> ConsumeChallengeAsync(string challengeId, CancellationToken ct = default);
}
