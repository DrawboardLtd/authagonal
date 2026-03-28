using Authagonal.Core.Services;

namespace Authagonal.Server.Services;

/// <summary>
/// Default secret provider that stores secrets as-is (no encryption).
/// Suitable for development; use <see cref="KeyVaultSecretProvider"/> in production.
/// </summary>
public sealed class PlaintextSecretProvider : ISecretProvider
{
    public Task<string> ResolveAsync(string secretReference, CancellationToken ct = default)
        => Task.FromResult(secretReference);

    public Task<string> ProtectAsync(string name, string plaintext, CancellationToken ct = default)
        => Task.FromResult(plaintext);
}
