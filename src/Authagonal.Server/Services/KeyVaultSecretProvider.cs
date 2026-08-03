using Authagonal.Core.Services;
using Azure.Security.KeyVault.Secrets;

namespace Authagonal.Server.Services;

/// <summary>
/// Stores and retrieves secrets from Azure Key Vault.
/// Secret references are stored as Key Vault secret names.
/// </summary>
public sealed class KeyVaultSecretProvider(
    SecretClient secretClient,
    SecretProviderOptions options,
    ILogger<KeyVaultSecretProvider> logger) : ISecretProvider
{
    private const string Prefix = "kv:";

    public async Task<string> ResolveAsync(string secretReference, CancellationToken ct = default)
    {
        // No kv: prefix means a legacy plaintext value written before this deployment moved to Key
        // Vault — the reference IS the secret. That passthrough is what makes a live migration
        // possible, and it is also a standing downgrade for as long as it is left open, which is
        // what SecretProvider:RequireVaultReferences exists to end. Note what the error deliberately
        // does NOT quote: in this branch the reference is the cleartext secret.
        if (!secretReference.StartsWith(Prefix, StringComparison.Ordinal))
        {
            if (options.RequireVaultReferences)
                throw new InvalidOperationException(
                    "Secret reference has no 'kv:' prefix and SecretProvider:RequireVaultReferences is set, " +
                    "so it will not be honoured as a plaintext value. Re-protect the secret through " +
                    "ISecretProvider so it is stored in Key Vault, or clear the setting while migrating.");

            return secretReference;
        }

        var secretName = secretReference[Prefix.Length..];
        var response = await secretClient.GetSecretAsync(secretName, cancellationToken: ct);
        return response.Value.Value;
    }

    public async Task<string> ProtectAsync(string name, string plaintext, CancellationToken ct = default)
    {
        var secretName = SanitizeName(name);

        await secretClient.SetSecretAsync(secretName, plaintext, ct);
        logger.LogInformation("Secret {SecretName} stored in Key Vault", secretName);

        return $"{Prefix}{secretName}";
    }

    /// <summary>
    /// Key Vault secret names must be 1-127 characters: alphanumeric and hyphens.
    /// </summary>
    /// <remarks>
    /// Injective, via <see cref="SecretNameSanitizer"/>. This used to fold every disallowed character to
    /// <c>'-'</c>, truncate to 127 characters BEFORE sanitising, and trim hyphens — three ways for two
    /// distinct names to become one Key Vault secret, which per <see cref="ISecretProvider"/>'s own contract
    /// means the second silently overwrites the first and both then resolve to the second.
    /// </remarks>
    private static string SanitizeName(string name)
        => SecretNameSanitizer.Sanitize(name, maxLength: 127, extraAllowed: "-");
}
