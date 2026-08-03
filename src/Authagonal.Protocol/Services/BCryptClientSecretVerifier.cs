using Authagonal.Core.Models;
using Authagonal.Core.Services;

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
            // Bounded and structurally checked before the library is handed anything — see BcryptHashFormat,
            // shared with the Server host's PasswordHasher so the two verifiers cannot drift apart again.
            // Without this, a `$2a$31$…` entry made every anonymous /connect/token call for that client an
            // uncancellable multi-day CPU burn, and a malformed one made it a permanent 500.
            if (!BcryptHashFormat.IsValid(hash))
                continue;

            try
            {
                if (BCrypt.Net.BCrypt.Verify(presentedSecret, hash))
                    return Task.FromResult(true);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Unverifiable hash — skip; may be handled by a different verifier in the host. Never a fault:
                // only SaltParseException was caught, and the pinned library throws FormatException,
                // ArgumentOutOfRangeException and IndexOutOfRangeException for other malformed shapes.
                continue;
            }
        }
        return Task.FromResult(false);
    }
}
