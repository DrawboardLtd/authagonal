using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Authagonal.Core.Services;
using Microsoft.Extensions.Logging;

namespace Authagonal.AwsProvider.Secrets;

/// <summary>
/// AWS Secrets Manager implementation of <see cref="ISecretProvider"/> — the substitute for Azure Key
/// Vault's <c>KeyVaultSecretProvider</c>. References are stored as <c>sm:{secretName}</c>; a reference
/// without the prefix is treated as a legacy plaintext value and returned unchanged, matching the Key
/// Vault provider's contract — unless <see cref="SecretProviderOptions.RequireVaultReferences"/> is
/// set, which turns that migration allowance into an error.
/// </summary>
public sealed class SecretsManagerSecretProvider(
    IAmazonSecretsManager client,
    SecretProviderOptions options,
    ILogger<SecretsManagerSecretProvider> logger) : ISecretProvider
{
    private const string Prefix = "sm:";

    public async Task<string> ResolveAsync(string secretReference, CancellationToken ct = default)
    {
        // No prefix → legacy plaintext value; the reference IS the value. Kept so a deployment can
        // migrate into Secrets Manager without rewriting its stored rows first, and closed by
        // SecretProvider:RequireVaultReferences once it has. The error never quotes the reference,
        // because on this branch the reference is the cleartext secret.
        if (!secretReference.StartsWith(Prefix, StringComparison.Ordinal))
        {
            if (options.RequireVaultReferences)
                throw new InvalidOperationException(
                    "Secret reference has no 'sm:' prefix and SecretProvider:RequireVaultReferences is set, " +
                    "so it will not be honoured as a plaintext value. Re-protect the secret through " +
                    "ISecretProvider so it is stored in Secrets Manager, or clear the setting while migrating.");

            return secretReference;
        }

        var name = secretReference[Prefix.Length..];
        var response = await client.GetSecretValueAsync(new GetSecretValueRequest { SecretId = name }, ct).ConfigureAwait(false);
        return response.SecretString;
    }

    public async Task<string> ProtectAsync(string name, string plaintext, CancellationToken ct = default)
    {
        var secretName = SanitizeName(name);

        try
        {
            await client.CreateSecretAsync(new CreateSecretRequest { Name = secretName, SecretString = plaintext }, ct).ConfigureAwait(false);
        }
        catch (ResourceExistsException)
        {
            // Already created — overwrite with a new version.
            await client.PutSecretValueAsync(new PutSecretValueRequest { SecretId = secretName, SecretString = plaintext }, ct).ConfigureAwait(false);
        }

        logger.LogInformation("Secret {SecretName} stored in Secrets Manager", secretName);
        return $"{Prefix}{secretName}";
    }

    /// <summary>
    /// Secrets Manager names allow <c>[A-Za-z0-9/_+=.@-]</c>, 1–512 chars. Map anything else to '-'.
    /// </summary>
    private static string SanitizeName(string name)
    {
        var max = Math.Min(name.Length, 512);
        var chars = new char[max];
        for (var i = 0; i < max; i++)
        {
            var c = name[i];
            chars[i] = char.IsLetterOrDigit(c) || c is '/' or '_' or '+' or '=' or '.' or '@' or '-' ? c : '-';
        }

        return new string(chars).Trim('-');
    }
}
