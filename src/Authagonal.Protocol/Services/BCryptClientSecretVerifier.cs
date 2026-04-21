using Authagonal.Core.Models;

namespace Authagonal.Protocol.Services;

/// <summary>
/// Default <see cref="IClientSecretVerifier"/> — assumes <see cref="OAuthClient.ClientSecretHashes"/>
/// contains BCrypt hashes. Good match for hosts that seed secrets via <see cref="OidcClientDescriptor"/>
/// (which BCrypt-hashes on seed).
/// </summary>
internal sealed class BCryptClientSecretVerifier : IClientSecretVerifier
{
    public Task<bool> VerifyAsync(OAuthClient client, string presentedSecret, CancellationToken ct = default)
    {
        foreach (var hash in client.ClientSecretHashes)
        {
            try
            {
                if (BCrypt.Net.BCrypt.Verify(presentedSecret, hash))
                    return Task.FromResult(true);
            }
            catch (BCrypt.Net.SaltParseException)
            {
                // Non-BCrypt hash — skip; may be handled by a different verifier in the host.
                continue;
            }
        }
        return Task.FromResult(false);
    }
}
