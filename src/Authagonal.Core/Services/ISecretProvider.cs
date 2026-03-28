namespace Authagonal.Core.Services;

/// <summary>
/// Abstracts secret storage so that secrets can be stored as plaintext (default)
/// or in a secure vault (e.g., Azure Key Vault).
/// </summary>
public interface ISecretProvider
{
    /// <summary>
    /// Resolves a secret reference to its plaintext value.
    /// For plaintext provider, the reference IS the value.
    /// For vault-backed providers, the reference is a URI/identifier that is fetched from the vault.
    /// </summary>
    Task<string> ResolveAsync(string secretReference, CancellationToken ct = default);

    /// <summary>
    /// Protects a plaintext secret, returning a reference that can later be resolved.
    /// For plaintext provider, returns the value unchanged.
    /// For vault-backed providers, stores the secret and returns a vault reference.
    /// </summary>
    Task<string> ProtectAsync(string name, string plaintext, CancellationToken ct = default);
}
