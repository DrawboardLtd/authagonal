using Authagonal.Core.Models;
using Authagonal.Protocol.Services;

namespace Authagonal.Server.Services;

/// <summary>
/// Server-side <see cref="IClientSecretVerifier"/> that delegates to
/// <see cref="PasswordHasher"/> so client-secret hashes share the same format pipeline as
/// user passwords (PBKDF2v1, ASP.NET Identity V3, legacy BCrypt). Registered ahead of
/// <c>AddAuthagonalProtocol</c> so the Protocol's BCrypt-only default is shadowed.
/// </summary>
public sealed class PasswordHasherClientSecretVerifier(PasswordHasher passwordHasher) : IClientSecretVerifier
{
    public Task<bool> VerifyAsync(OAuthClient client, string secret, CancellationToken ct = default)
    {
        foreach (var hash in client.ClientSecretHashes)
        {
            var result = passwordHasher.VerifyPassword(secret, hash);
            if (result is PasswordVerifyResult.Success or PasswordVerifyResult.SuccessRehashNeeded)
                return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }
}
