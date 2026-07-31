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
