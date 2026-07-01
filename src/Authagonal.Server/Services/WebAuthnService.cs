using System.Text;
using System.Text.Json;
using Authagonal.Core.Models;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.AspNetCore.Http;

namespace Authagonal.Server.Services;

public sealed class WebAuthnService(IHttpContextAccessor httpContextAccessor)
{
    // Build the FIDO2 relying-party config from the ACTUAL request host, not a fixed startup value.
    // WebAuthn requires the RP ID to be the origin's registrable host and the origin to match exactly,
    // so on a multi-tenant server (each tenant on its own host, e.g. {slug}-admin.authagonal.io) the RP
    // must be resolved per request — a single startup Issuer would only ever be valid for one host, which
    // is why passkey setup failed for every tenant host. rp.id is the hostname (no port); the origin is
    // scheme + host (+ port) and must equal what the browser sends.
    private IFido2 ResolveFido2()
    {
        var req = httpContextAccessor.HttpContext?.Request
            ?? throw new InvalidOperationException("WebAuthn requires an active HTTP request.");
        var origin = $"{req.Scheme}://{req.Host.Value}";
        return new Fido2(new Fido2Configuration
        {
            ServerDomain = req.Host.Host,
            ServerName = "Authagonal",
            Origins = new HashSet<string> { origin },
        });
    }

    public (CredentialCreateOptions Options, string SetupToken) CreateAttestationOptions(
        AuthUser user, IReadOnlyList<MfaCredential> existingCredentials)
    {
        var fidoUser = new Fido2User
        {
            Id = Encoding.UTF8.GetBytes(user.Id),
            Name = user.Email,
            DisplayName = $"{user.FirstName} {user.LastName}".Trim(),
        };

        var excludeCredentials = existingCredentials
            .Where(c => c.Type == MfaCredentialType.WebAuthn && c.PublicKeyJson is not null)
            .Select(c =>
            {
                var data = JsonSerializer.Deserialize(c.PublicKeyJson!, AuthagonalJsonContext.Default.WebAuthnCredentialData);
                return new PublicKeyCredentialDescriptor(Convert.FromBase64String(data!.CredentialId));
            })
            .ToList();

        var options = ResolveFido2().RequestNewCredential(
            new RequestNewCredentialParams
            {
                User = fidoUser,
                ExcludeCredentials = excludeCredentials,
                AuthenticatorSelection = new AuthenticatorSelection
                {
                    ResidentKey = ResidentKeyRequirement.Preferred,
                    UserVerification = UserVerificationRequirement.Preferred,
                },
                AttestationPreference = AttestationConveyancePreference.None,
            });

        var setupToken = Guid.NewGuid().ToString("N");
        return (options, setupToken);
    }

    public async Task<MfaCredential> CompleteAttestationAsync(
        string userId,
        CredentialCreateOptions originalOptions,
        AuthenticatorAttestationRawResponse attestationResponse,
        CancellationToken ct = default)
    {
        var result = await ResolveFido2().MakeNewCredentialAsync(
            new MakeNewCredentialParams
            {
                AttestationResponse = attestationResponse,
                OriginalOptions = originalOptions,
                IsCredentialIdUniqueToUserCallback = (args, _) => Task.FromResult(true),
            }, ct);

        var credData = new WebAuthnCredentialData
        {
            CredentialId = Convert.ToBase64String(result.Id),
            PublicKey = Convert.ToBase64String(result.PublicKey),
            CredType = result.Type.ToString(),
            Aaguid = result.AaGuid.ToString(),
        };

        return new MfaCredential
        {
            Id = Guid.NewGuid().ToString("N"),
            UserId = userId,
            Type = MfaCredentialType.WebAuthn,
            Name = "Passkey",
            PublicKeyJson = JsonSerializer.Serialize(credData, AuthagonalJsonContext.Default.WebAuthnCredentialData),
            SignCount = (uint)result.SignCount,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    public AssertionOptions CreateAssertionOptions(IReadOnlyList<MfaCredential> webAuthnCredentials)
    {
        var allowedCredentials = webAuthnCredentials
            .Where(c => c.Type == MfaCredentialType.WebAuthn && c.PublicKeyJson is not null)
            .Select(c =>
            {
                var data = JsonSerializer.Deserialize(c.PublicKeyJson!, AuthagonalJsonContext.Default.WebAuthnCredentialData);
                return new PublicKeyCredentialDescriptor(Convert.FromBase64String(data!.CredentialId));
            })
            .ToList();

        return ResolveFido2().GetAssertionOptions(
            new GetAssertionOptionsParams
            {
                AllowedCredentials = allowedCredentials,
                UserVerification = UserVerificationRequirement.Preferred,
            });
    }

    public async Task<(bool Success, byte[] CredentialId, uint NewSignCount)> CompleteAssertionAsync(
        AssertionOptions originalOptions,
        AuthenticatorAssertionRawResponse assertionResponse,
        byte[] storedPublicKey,
        uint storedSignCount,
        CancellationToken ct = default)
    {
        var result = await ResolveFido2().MakeAssertionAsync(
            new MakeAssertionParams
            {
                AssertionResponse = assertionResponse,
                OriginalOptions = originalOptions,
                StoredPublicKey = storedPublicKey,
                StoredSignatureCounter = storedSignCount,
                IsUserHandleOwnerOfCredentialIdCallback = (args, _) => Task.FromResult(true),
            }, ct);

        return (true, result.CredentialId, (uint)result.SignCount);
    }
}

public sealed class WebAuthnCredentialData
{
    public required string CredentialId { get; set; }
    public required string PublicKey { get; set; }
    public required string CredType { get; set; }
    public required string Aaguid { get; set; }
}
