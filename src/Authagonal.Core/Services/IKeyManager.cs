using Microsoft.IdentityModel.Tokens;

namespace Authagonal.Core.Services;

/// <summary>
/// Provides signing credentials and JSON Web Keys for token creation and validation.
/// Single-tenant deployments use the default <c>KeyManager</c> singleton.
/// Multi-tenant deployments provide per-tenant implementations.
/// </summary>
public interface IKeyManager
{
    SigningCredentials GetSigningCredentials();
    IReadOnlyList<JsonWebKey> GetSecurityKeys();
}
