using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Authagonal.Core.Services;
using Microsoft.Extensions.Logging;

namespace Authagonal.AwsProvider.Secrets;

/// <summary>
/// AWS Secrets Manager implementation of <see cref="ISecretProvider"/> — the substitute for Azure Key
/// Vault's <c>KeyVaultSecretProvider</c>. References are stored as <c>sm:{secretName}</c>; a reference
/// without the prefix is treated as a legacy plaintext value and returned unchanged, matching the Key
/// Vault provider's contract.
/// </summary>
public sealed class SecretsManagerSecretProvider(IAmazonSecretsManager client, ILogger<SecretsManagerSecretProvider> logger) : ISecretProvider
{
    private const string Prefix = "sm:";

    public async Task<string> ResolveAsync(string secretReference, CancellationToken ct = default)
    {
        // No prefix → legacy plaintext value; the reference IS the value.
        if (!secretReference.StartsWith(Prefix, StringComparison.Ordinal))
            return secretReference;

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
