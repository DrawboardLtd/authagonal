using Authagonal.Core.Models;
using Authagonal.Core.Stores;
using Authagonal.Protocol.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Authagonal.Server.Services;

/// <summary>
/// Server-side <see cref="IClientSecretVerifier"/> that delegates to
/// <see cref="PasswordHasher"/> so client-secret hashes share the same format pipeline as
/// user passwords (PBKDF2v2, ASP.NET Identity V3, legacy BCrypt). Registered ahead of
/// <c>AddAuthagonalProtocol</c> so the Protocol's BCrypt-only default is shadowed.
/// </summary>
public sealed class PasswordHasherClientSecretVerifier(
    PasswordHasher passwordHasher,
    IServiceProvider? services = null,
    ILogger<PasswordHasherClientSecretVerifier>? logger = null) : IClientSecretVerifier
{
    public async Task<bool> VerifyAsync(OAuthClient client, string secret, CancellationToken ct = default)
    {
        for (var i = 0; i < client.ClientSecretHashes.Count; i++)
        {
            var result = passwordHasher.VerifyPassword(secret, client.ClientSecretHashes[i]);

            if (result == PasswordVerifyResult.Success)
                return true;

            if (result == PasswordVerifyResult.SuccessRehashNeeded)
            {
                // The signal was computed and then thrown away, so a Duende-migrated client stayed on
                // a bare unsalted SHA-256 of its secret forever — a format that is trivially
                // rainbow-tabled from a store dump, with no upgrade path, no runtime warning, and no
                // way for an operator to even find out which clients were affected (the admin API
                // redacts hashes). The doc comment on VerifyTaggedDigest said as much: the legacy
                // format "lives until the secret is rotated".
                //
                // The plaintext secret is in hand at exactly this moment, which is the only moment it
                // ever is — the same upgrade the login path performs for user passwords.
                await UpgradeAsync(client, i, secret, ct).ConfigureAwait(false);
                return true;
            }
        }

        return false;
    }

    private async Task UpgradeAsync(OAuthClient client, int index, string secret, CancellationToken ct)
    {
        var clientStore = services?.GetService<IClientStore>();
        if (clientStore is null) return;

        try
        {
            // Re-read so a concurrent admin edit is not clobbered by this write, and so the upgrade
            // is skipped when another node has already done it.
            var current = await clientStore.GetAsync(client.ClientId, ct).ConfigureAwait(false);
            if (current is null || index >= current.ClientSecretHashes.Count) return;
            if (!string.Equals(current.ClientSecretHashes[index], client.ClientSecretHashes[index], StringComparison.Ordinal))
                return;

            var upgraded = new List<string>(current.ClientSecretHashes);
            upgraded[index] = passwordHasher.HashPassword(secret);
            current.ClientSecretHashes = upgraded;

            await clientStore.UpsertAsync(current, ct).ConfigureAwait(false);

            // Keep the in-hand copy consistent so the caller does not carry a stale hash onward.
            client.ClientSecretHashes = upgraded;

            logger?.LogInformation(
                "Upgraded a legacy client-secret hash for {ClientId} to the current format", client.ClientId);
        }
        catch (Exception ex)
        {
            // Authentication has already succeeded. A failed upgrade must not turn a valid credential
            // into a rejected one — it just means the next call tries again.
            logger?.LogWarning(ex,
                "Could not upgrade the legacy client-secret hash for {ClientId}; it remains on the legacy format",
                client.ClientId);
        }
    }
}
