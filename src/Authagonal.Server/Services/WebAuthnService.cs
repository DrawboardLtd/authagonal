using System.Text;
using System.Text.Json;
using Authagonal.Core.Models;
using Authagonal.Core.Stores;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.AspNetCore.Http;

namespace Authagonal.Server.Services;

public sealed class WebAuthnService(
    IHttpContextAccessor httpContextAccessor,
    IMfaStore mfaStore,
    Microsoft.Extensions.Options.IOptions<AuthOptions> authOptions,
    Microsoft.Extensions.Logging.ILogger<WebAuthnService>? logger = null)
{
    /// <summary>
    /// Process-wide latch so the unset-allowlist warning is emitted once, not per ceremony.
    /// </summary>
    private static int _gapWarned;

    /// <summary>
    /// The relying-party ID this request would act as: the request host, once it has been checked
    /// against the allowlist.
    /// </summary>
    private string ResolveRpId()
    {
        var req = httpContextAccessor.HttpContext?.Request
            ?? throw new InvalidOperationException("WebAuthn requires an active HTTP request.");

        var allowed = authOptions.Value.WebAuthnAllowedHosts;
        if (allowed.Count == 0)
        {
            // The gap this class's remarks promise to log. It said "it is logged as a gap instead" and
            // docs/mfa.md repeats the claim, but nothing logged anything — so the one signal an operator
            // had that the RP ID is unconstrained was a sentence in a source comment. A silent fail-open
            // that documents itself as noisy is worse than either honest option.
            if (System.Threading.Interlocked.Exchange(ref _gapWarned, 1) == 0)
                logger?.LogWarning(
                    "Auth:WebAuthnAllowedHosts is empty, so any Host header this server answers becomes " +
                    "the WebAuthn relying-party ID and expected origin — both ceremonies then check the " +
                    "request against itself. Set it to the hostnames you serve (and set AllowedHosts in " +
                    "configuration so host filtering rejects the rest before any handler runs).");
        }
        else if (!allowed.Contains(req.Host.Host, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Host '{req.Host.Host}' is not in Auth:WebAuthnAllowedHosts, so it cannot act as a " +
                "WebAuthn relying party. A credential registered under one RP ID must never be usable " +
                "under another.");
        }

        return req.Host.Host;
    }

    // Build the FIDO2 relying-party config from the ACTUAL request host, not a fixed startup value.
    // WebAuthn requires the RP ID to be the origin's registrable host and the origin to match exactly,
    // so on a multi-tenant server (each tenant on its own host, e.g. {slug}-admin.authagonal.io) the RP
    // must be resolved per request — a single startup Issuer would only ever be valid for one host, which
    // is why passkey setup failed for every tenant host. rp.id is the hostname (no port); the origin is
    // scheme + host (+ port) and must equal what the browser sends.
    /// <remarks>
    /// The per-request host is now checked against a configured allowlist first.
    /// <para>
    /// Resolving the RP per request is necessary for multi-tenant hosting, but taking it from the
    /// request with nothing to check it against made both WebAuthn ceremonies self-referential: §7.1
    /// steps 9/13 and §7.2 steps 13/14 compare the client's origin and rpIdHash against an expectation
    /// that came from the same request. The library performs the comparison faithfully — against a
    /// value the caller supplied. With <c>AllowedHosts</c> shipped as "*", nothing constrained the Host
    /// header either.
    /// </para>
    /// <para>
    /// An empty allowlist keeps the previous behaviour, because refusing every ceremony on upgrade
    /// would lock out every existing passkey user; it is logged as a gap instead. Set
    /// <c>Auth:WebAuthnAllowedHosts</c> to the real host list.
    /// </para>
    /// </remarks>
    private IFido2 ResolveFido2()
    {
        var rpId = ResolveRpId();
        var req = httpContextAccessor.HttpContext!.Request;
        var origin = $"{req.Scheme}://{req.Host.Value}";
        return new Fido2(new Fido2Configuration
        {
            ServerDomain = rpId,
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
        // §7.1 step 22 — the credential id must not already be registered. This was hardcoded to true,
        // which does not satisfy the check, it removes it: the library only ever asked, and the answer
        // was always "unique". The real answer is the index row, and asking here means the verdict is
        // reached inside the same verification call rather than by a separate read the caller might
        // forget. Note it is unique across the WHOLE store, not just this user — a duplicate belonging
        // to the SAME user is just as damaging as a cross-user one: a second credential row for one
        // authenticator, its sign counter reset to the attestation's, and one shared index row that
        // either credential's deletion removes.
        var result = await ResolveFido2().MakeNewCredentialAsync(
            new MakeNewCredentialParams
            {
                AttestationResponse = attestationResponse,
                OriginalOptions = originalOptions,
                IsCredentialIdUniqueToUserCallback = async (args, token) =>
                    await mfaStore.FindByWebAuthnCredentialIdAsync(args.CredentialId, token) is null,
            }, ct);

        var credData = new WebAuthnCredentialData
        {
            CredentialId = Convert.ToBase64String(result.Id),
            PublicKey = Convert.ToBase64String(result.PublicKey),
            CredType = result.Type.ToString(),
            Aaguid = result.AaGuid.ToString(),
            // Record WHICH relying party this credential was created for. Nothing was recorded before,
            // so at assertion the expected RP ID could only be recomputed from the request being
            // verified — the check compared a request against itself and the server contributed no
            // independent assurance about where the ceremony happened. Stored here, it becomes the one
            // input to the comparison the caller cannot influence.
            RpId = ResolveRpId(),
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

    /// <summary>
    /// Builds assertion options for a passkey challenge.
    /// </summary>
    /// <param name="requireUserVerification">
    /// True for PASSWORDLESS sign-in, where the passkey is the only factor: user verification (a PIN,
    /// biometric or device unlock) is what makes the assertion two-factor rather than mere possession of an
    /// unlocked device. Fido2 enforces the requirement itself during <c>MakeAssertionAsync</c>, so setting
    /// it here is load-bearing rather than advisory.
    ///
    /// False (the default) for the SECOND-FACTOR path, where a password has already been presented, so
    /// possession alone is a genuine second factor and Preferred keeps older authenticators working.
    /// </param>
    /// <remarks>
    /// This was <c>Preferred</c> everywhere and the resulting UV flag was never inspected, while
    /// passwordless sign-in marked the session <c>mfa_authenticated</c> and the docs described it as strong
    /// authentication. A passkey asserted without UV is single-factor possession, so a stolen unlocked
    /// device satisfied an MFA-required policy. A credential that cannot do UV is now simply unusable
    /// passwordlessly — it remains usable as a second factor.
    /// </remarks>
    public AssertionOptions CreateAssertionOptions(
        IReadOnlyList<MfaCredential> webAuthnCredentials,
        bool requireUserVerification = false)
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
                UserVerification = requireUserVerification
                    ? UserVerificationRequirement.Required
                    : UserVerificationRequirement.Preferred,
            });
    }

    /// <param name="expectedUserId">
    /// The account this assertion must belong to — the challenged user for second-factor verification,
    /// the credential's indexed owner for discoverable (passwordless) login.
    /// </param>
    /// <param name="registeredRpId">
    /// The relying party the credential was registered under, as recorded at attestation. When present it
    /// must equal the RP ID this request resolves to.
    /// </param>
    /// <remarks>
    /// This is the check the request cannot answer for itself. §7.2 step 13 compares the assertion's
    /// rpIdHash against an expectation built from the request's own Host header, so with
    /// <c>AllowedHosts</c> "*" a man-in-the-middle that forwards <c>Host: proxy.example</c> gets an
    /// expectation of proxy.example, and WebAuthn's origin binding — the property that makes a passkey
    /// phishing-resistant — verifies the phishing site instead of preventing it. A credential enrolled
    /// at the real host carries that host, so the mismatch is caught from storage.
    /// <para>
    /// Null for credentials enrolled before the RP ID was recorded: those are grandfathered rather than
    /// bricked, and gain the binding when they are re-enrolled.
    /// </para>
    /// </remarks>
    public async Task<(bool Success, byte[] CredentialId, uint NewSignCount)> CompleteAssertionAsync(
        AssertionOptions originalOptions,
        AuthenticatorAssertionRawResponse assertionResponse,
        byte[] storedPublicKey,
        uint storedSignCount,
        string expectedUserId,
        string? registeredRpId = null,
        CancellationToken ct = default)
    {
        // Reported as a failed assertion rather than an exception: it is the same class of failure as the
        // library's own rpIdHash check (§7.2 step 13), and both callers already map a false result to 401.
        if (!string.IsNullOrEmpty(registeredRpId)
            && !string.Equals(registeredRpId, ResolveRpId(), StringComparison.OrdinalIgnoreCase))
        {
            logger?.LogWarning(
                "Refusing a passkey assertion: the credential was registered for relying party " +
                "{RegisteredRpId}, but this request resolves to a different one", registeredRpId);
            return (false, [], 0);
        }

        // §7.2 step 6 — the user handle the authenticator returned must belong to the account being
        // signed in. This was hardcoded to true, so the check the library offered was answered without
        // being performed. The handle is UTF-8 of the user id (see CreateAttestationOptions), so the
        // comparison is direct. Pinning it to the account the caller already established, rather than
        // to whatever the credential-id index happens to say, is what makes the handle a genuine
        // tiebreaker: it is the layer that would catch an ambiguous or repointed index instead of
        // trusting it. The library only invokes this when a handle is present, which is why the
        // passwordless endpoint — where the spec makes the handle mandatory — also requires it there.
        var expectedHandle = Encoding.UTF8.GetBytes(expectedUserId);
        var result = await ResolveFido2().MakeAssertionAsync(
            new MakeAssertionParams
            {
                AssertionResponse = assertionResponse,
                OriginalOptions = originalOptions,
                StoredPublicKey = storedPublicKey,
                StoredSignatureCounter = storedSignCount,
                IsUserHandleOwnerOfCredentialIdCallback = (args, _) =>
                    Task.FromResult(args.UserHandle.AsSpan().SequenceEqual(expectedHandle)),
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

    /// <summary>
    /// Relying-party ID (the host) the credential was enrolled under. Null on credentials stored before
    /// this was recorded — see <see cref="WebAuthnService.CompleteAssertionAsync"/> for why those are
    /// grandfathered rather than refused.
    /// </summary>
    public string? RpId { get; set; }
}
