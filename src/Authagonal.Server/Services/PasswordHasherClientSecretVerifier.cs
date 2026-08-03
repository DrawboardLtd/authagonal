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
        if (services is null) return;

        try
        {
            // Resolved from a SCOPE, and inside the try.
            //
            // This class is registered TryAddSingleton, so the injected provider is the ROOT provider —
            // and GetService<IClientStore>() against the root throws for a store registered AddScoped,
            // which is how every multi-tenant host registers it (the tenant is a scoped concern). The
            // resolution sat outside the try, so the exception escaped VerifyAsync after the presented
            // secret had already verified: correct client_credentials answered 500 instead of a token,
            // on /connect/token, /par, /introspect, /revocation and /deviceauthorization. Permanent,
            // because the upgrade could never complete, and triggered by the legitimate credential.
            //
            // Exactly the defect LegacySecretHashWarning had — it prevented any tenant-scoped host from
            // starting until it resolved the store inside a scope — reintroduced one file over.
            using var scope = services.CreateScope();
            var clientStore = scope.ServiceProvider.GetService<IClientStore>();
            if (clientStore is null) return;

            // A CONDITIONAL write of the one entry, not a full-record upsert of a snapshot. The previous
            // version re-read the record and then wrote the whole thing back unconditionally, so an admin
            // write landing between the read and the write was reverted — including a secret rotation, which
            // left the compromised secret working while the audit log recorded a successful rotation. See
            // IClientStore.TryUpgradeSecretHashAsync.
            var expected = client.ClientSecretHashes[index];
            var upgradedHash = passwordHasher.HashPassword(secret);

            if (!await clientStore.TryUpgradeSecretHashAsync(
                    client.ClientId, index, expected, upgradedHash, ct).ConfigureAwait(false))
            {
                // Either the record moved underneath us — in which case whoever wrote it wins — or this
                // backend has no conditional primitive. Both mean: leave the legacy hash, try again next
                // time. LegacySecretHashWarning is what tells the operator it is still there.
                logger?.LogDebug(
                    "Left the legacy client-secret hash for {ClientId} in place: the conditional upgrade did "
                    + "not apply", client.ClientId);
                return;
            }

            // Keep the in-hand copy consistent so the caller does not carry a stale hash onward.
            var upgraded = new List<string>(client.ClientSecretHashes) { [index] = upgradedHash };
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
