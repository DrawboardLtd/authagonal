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
