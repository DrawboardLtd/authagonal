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
    Task StoreWebAuthnCredentialIdMappingAsync(byte[] webAuthnCredentialId, string userId, string credentialId, CancellationToken ct = default);
    Task DeleteWebAuthnCredentialIdMappingAsync(byte[] webAuthnCredentialId, CancellationToken ct = default);

    // Challenges
    Task StoreChallengeAsync(MfaChallenge challenge, CancellationToken ct = default);
    Task<MfaChallenge?> GetChallengeAsync(string challengeId, CancellationToken ct = default);
    Task<MfaChallenge?> ConsumeChallengeAsync(string challengeId, CancellationToken ct = default);
}
