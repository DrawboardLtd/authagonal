using Authagonal.Core.Models;

namespace Authagonal.Protocol.Services;

/// <summary>
/// Verifies a presented client secret against the stored hashes on an <see cref="OAuthClient"/>.
/// Protocol ships a BCrypt default; hosts that already have a password hasher (e.g. Authagonal.Server)
/// can register their own implementation.
/// </summary>
public interface IClientSecretVerifier
{
    Task<bool> VerifyAsync(OAuthClient client, string presentedSecret, CancellationToken ct = default);
}
