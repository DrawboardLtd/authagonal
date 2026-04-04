using Authagonal.Core.Services;
using Azure.Security.KeyVault.Secrets;

namespace Authagonal.Server.Services;

/// <summary>
/// Stores and retrieves secrets from Azure Key Vault.
/// Secret references are stored as Key Vault secret names.
/// </summary>
public sealed class KeyVaultSecretProvider(
    SecretClient secretClient,
    ILogger<KeyVaultSecretProvider> logger) : ISecretProvider
{
    private const string Prefix = "kv:";

    public async Task<string> ResolveAsync(string secretReference, CancellationToken ct = default)
    {
        // If the reference doesn't have the kv: prefix, it's a legacy plaintext value
        if (!secretReference.StartsWith(Prefix, StringComparison.Ordinal))
            return secretReference;

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
    private static string SanitizeName(string name)
    {
        var sanitized = new char[Math.Min(name.Length, 127)];
        for (var i = 0; i < sanitized.Length; i++)
        {
            var c = name[i];
            sanitized[i] = char.IsLetterOrDigit(c) ? c : '-';
        }

        return new string(sanitized).Trim('-');
    }
}
