using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Authagonal.Core.Models;
using Authagonal.Core.Stores;
using Authagonal.Server;
using Authagonal.Server.Services;
using Authagonal.Tests.Infrastructure;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.AspNetCore.Http;

namespace Authagonal.Tests;

/// <summary>
/// End-to-end WebAuthn (passkey) verification through <see cref="WebAuthnService"/>, driven by a
/// software authenticator that produces cryptographically valid attestation/assertion responses.
/// This exercises the real fido2-net-lib verification path — signature, challenge binding, RP-ID
/// hash, origin, and signature-counter checks — rather than opaque recorded blobs, so tampering,
/// wrong-origin, wrong-challenge, and counter-rollback cases must all be rejected.
/// </summary>
public sealed class WebAuthnRoundTripTests
{
    private const string RpId = "test.authagonal.local";
    private const string Origin = "https://test.authagonal.local";

    // WebAuthnService now derives the FIDO2 relying party from the live request host (per-tenant hosts),
    // so drive it with an HTTP context whose host is RpId — giving ServerDomain=RpId and origin=Origin,
    // exactly what the VirtualAuthenticator signs over. The store backs the credential-id uniqueness
    // callback, so a test that wants an id to look already-registered seeds it there.
    private static WebAuthnService NewService(IMfaStore? store = null, params string[] allowedHosts)
        => NewServiceAt(RpId, store, allowedHosts);

    /// <summary>The same service answering a request for <paramref name="host"/>.</summary>
    private static WebAuthnService NewServiceAt(string host, IMfaStore? store = null, params string[] allowedHosts)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Scheme = "https";
        ctx.Request.Host = new HostString(host);
        return new WebAuthnService(
            new StubHttpContextAccessor { HttpContext = ctx },
            store ?? new InMemoryMfaStore(),
            Microsoft.Extensions.Options.Options.Create(new AuthOptions
            {
                WebAuthnAllowedHosts = [.. allowedHosts],
            }));
    }

    /// <summary>
    /// A host outside the allowlist cannot act as a relying party.
    /// </summary>
    /// <remarks>
    /// Without this the RP ID and expected origin came from the request's own Host header, so the
    /// origin and rpIdHash checks compared a request against itself. This test would pass against the
    /// old code only because the harness happens to fix the host — which is precisely the property a
    /// real deployment does not have, with AllowedHosts shipped as "*".
    /// </remarks>
    [Fact]
    public void ResolvingAnUnlistedHost_IsRefused()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Scheme = "https";
        ctx.Request.Host = new HostString("attacker.example");
        var service = new WebAuthnService(
            new StubHttpContextAccessor { HttpContext = ctx },
            new InMemoryMfaStore(),
            Microsoft.Extensions.Options.Options.Create(new AuthOptions
            {
                WebAuthnAllowedHosts = [RpId],
            }));

        Assert.Throws<InvalidOperationException>(() =>
            service.CreateAttestationOptions(new AuthUser { Id = "u1", Email = "u@example.com", NormalizedEmail = "U@EXAMPLE.COM" }, []));
    }

    /// <summary>
    /// A credential enrolled at one host cannot be asserted at another, even with material that verifies.
    /// </summary>
    /// <remarks>
    /// This is the residual the allowlist does not cover, because the allowlist is empty by default and a
    /// multi-tenant deployment legitimately serves many hosts. Both ceremonies build their expectations
    /// from the request's own Host header, so §7.2 steps 13/14 compared a request against itself and the
    /// server contributed nothing: an on-path host that forwards <c>Host: proxy.example</c> gets an
    /// expectation of proxy.example, and the origin binding that makes a passkey phishing-resistant
    /// verifies the phishing host rather than preventing it. The authenticator below signs for the second
    /// host with the SAME key, so every check the library performs passes — which is precisely why the
    /// answer has to come from what was recorded at enrolment, the one input the request cannot supply.
    /// </remarks>
    [Fact]
    public async Task ACredentialEnrolledAtOneHost_IsRefusedAtAnother()
    {
        const string OtherRpId = "proxy.example";
        const string OtherOrigin = "https://proxy.example";
        var store = new InMemoryMfaStore();
        var user = TestUser();

        var enrol = NewServiceAt(RpId, store, RpId, OtherRpId);
        var auth = new VirtualAuthenticator(RpId, Origin);
        var (opts, _) = enrol.CreateAttestationOptions(user, []);
        var cred = await enrol.CompleteAttestationAsync(user.Id, opts, auth.Attestation(opts.Challenge));

        var stored = JsonSerializer.Deserialize(cred.PublicKeyJson!, AuthagonalJsonContext.Default.WebAuthnCredentialData)!;
        Assert.Equal(RpId, stored.RpId);

        // Same key, signing for the other host: internally consistent, verifies against the enrolled
        // public key, and matches everything that host's request would lead the library to expect.
        var elsewhere = NewServiceAt(OtherRpId, store, RpId, OtherRpId);
        var assertOpts = elsewhere.CreateAssertionOptions([cred]);
        var (success, _, _) = await elsewhere.CompleteAssertionAsync(
            assertOpts,
            auth.Assertion(assertOpts.Challenge, signCount: 1, userHandle: Handle(user.Id),
                asRpId: OtherRpId, asOrigin: OtherOrigin),
            StoredPublicKey(cred), storedSignCount: 0, expectedUserId: user.Id,
            registeredRpId: stored.RpId);

        Assert.False(success);
    }

    /// <summary>
    /// The same credential at its own host still works — the binding must not break normal use.
    /// </summary>
    [Fact]
    public async Task ACredentialEnrolledAtOneHost_StillAssertsThere()
    {
        var store = new InMemoryMfaStore();
        var user = TestUser();
        var svc = NewServiceAt(RpId, store, RpId);
        var auth = new VirtualAuthenticator(RpId, Origin);

        var (opts, _) = svc.CreateAttestationOptions(user, []);
        var cred = await svc.CompleteAttestationAsync(user.Id, opts, auth.Attestation(opts.Challenge));
        var stored = JsonSerializer.Deserialize(cred.PublicKeyJson!, AuthagonalJsonContext.Default.WebAuthnCredentialData)!;

        var assertOpts = svc.CreateAssertionOptions([cred]);
        var (success, _, _) = await svc.CompleteAssertionAsync(
            assertOpts, auth.Assertion(assertOpts.Challenge, signCount: 1, userHandle: Handle(user.Id)),
            StoredPublicKey(cred), storedSignCount: 0, expectedUserId: user.Id, registeredRpId: stored.RpId);

        Assert.True(success);
    }

    /// <summary>
    /// A credential enrolled before the RP ID was recorded is grandfathered, not bricked.
    /// </summary>
    [Fact]
    public async Task ACredentialWithNoRecordedRpId_StillAsserts()
    {
        var store = new InMemoryMfaStore();
        var user = TestUser();
        var svc = NewServiceAt(RpId, store, RpId);
        var auth = new VirtualAuthenticator(RpId, Origin);

        var (opts, _) = svc.CreateAttestationOptions(user, []);
        var cred = await svc.CompleteAttestationAsync(user.Id, opts, auth.Attestation(opts.Challenge));

        var assertOpts = svc.CreateAssertionOptions([cred]);
        var (success, _, _) = await svc.CompleteAssertionAsync(
            assertOpts, auth.Assertion(assertOpts.Challenge, signCount: 1, userHandle: Handle(user.Id)),
            StoredPublicKey(cred), storedSignCount: 0, expectedUserId: user.Id, registeredRpId: null);

        Assert.True(success);
    }

    private sealed class StubHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    private static AuthUser TestUser() => new()
    {
        Id = "user-webauthn-1",
        Email = "passkey@acme.test",
        NormalizedEmail = "PASSKEY@ACME.TEST",
        FirstName = "Pass",
        LastName = "Key",
    };

    // The handle a real authenticator returns for an enrolled credential: UTF-8 of the user id, as
    // CreateAttestationOptions sets it. Passed explicitly at every call site rather than defaulted
    // inside the authenticator, because Fido2NetLib only invokes the ownership callback when a handle
    // is present — a test that silently dropped it would stop exercising §7.2 step 6 at all.
    private static byte[] Handle(string userId) => Encoding.UTF8.GetBytes(userId);

    private static byte[] StoredPublicKey(MfaCredential cred)
    {
        var data = JsonSerializer.Deserialize(cred.PublicKeyJson!, AuthagonalJsonContext.Default.WebAuthnCredentialData)!;
        return Convert.FromBase64String(data.PublicKey);
    }

    [Fact]
    public async Task Attestation_ThenAssertion_RoundTrips()
    {
        var svc = NewService();
        var auth = new VirtualAuthenticator(RpId, Origin);
        var user = TestUser();

        var (opts, _) = svc.CreateAttestationOptions(user, []);
        var cred = await svc.CompleteAttestationAsync(user.Id, opts, auth.Attestation(opts.Challenge));

        Assert.Equal(MfaCredentialType.WebAuthn, cred.Type);
        Assert.NotNull(cred.PublicKeyJson);
        Assert.Equal(0u, cred.SignCount);
        var stored = JsonSerializer.Deserialize(cred.PublicKeyJson!, AuthagonalJsonContext.Default.WebAuthnCredentialData)!;
        Assert.Equal(auth.CredentialIdB64, stored.CredentialId);

        var assertOpts = svc.CreateAssertionOptions([cred]);
        var (success, credentialId, newSignCount) = await svc.CompleteAssertionAsync(
            assertOpts, auth.Assertion(assertOpts.Challenge, signCount: 1, userHandle: Handle("user-webauthn-1")), StoredPublicKey(cred),
            storedSignCount: 0, expectedUserId: user.Id);

        Assert.True(success);
        Assert.Equal(1u, newSignCount);
        Assert.Equal(auth.CredentialId, credentialId);
    }

    [Fact]
    public async Task Assertion_WithTamperedSignature_IsRejected()
    {
        var svc = NewService();
        var auth = new VirtualAuthenticator(RpId, Origin);
        var (opts, _) = svc.CreateAttestationOptions(TestUser(), []);
        var cred = await svc.CompleteAttestationAsync("user-webauthn-1", opts, auth.Attestation(opts.Challenge));
        var assertOpts = svc.CreateAssertionOptions([cred]);

        await Assert.ThrowsAnyAsync<Exception>(() => svc.CompleteAssertionAsync(
            assertOpts, auth.Assertion(assertOpts.Challenge, signCount: 1, tamperSignature: true, userHandle: Handle("user-webauthn-1")),
            StoredPublicKey(cred), storedSignCount: 0, expectedUserId: "user-webauthn-1"));
    }

    [Fact]
    public async Task Attestation_WithWrongChallenge_IsRejected()
    {
        var svc = NewService();
        var auth = new VirtualAuthenticator(RpId, Origin);
        var (opts, _) = svc.CreateAttestationOptions(TestUser(), []);

        // Sign over a challenge the relying party never issued.
        var bogus = RandomNumberGenerator.GetBytes(32);
        await Assert.ThrowsAnyAsync<Exception>(() => svc.CompleteAttestationAsync(
            "user-webauthn-1", opts, auth.Attestation(opts.Challenge, overrideChallenge: bogus)));
    }

    [Fact]
    public async Task Attestation_WithWrongOrigin_IsRejected()
    {
        var svc = NewService();
        var auth = new VirtualAuthenticator(RpId, Origin);
        var (opts, _) = svc.CreateAttestationOptions(TestUser(), []);

        await Assert.ThrowsAnyAsync<Exception>(() => svc.CompleteAttestationAsync(
            "user-webauthn-1", opts, auth.Attestation(opts.Challenge, overrideOrigin: "https://evil.example.com")));
    }

    [Fact]
    public async Task Assertion_WithRolledBackSignCounter_IsRejected()
    {
        var svc = NewService();
        var auth = new VirtualAuthenticator(RpId, Origin);
        var (opts, _) = svc.CreateAttestationOptions(TestUser(), []);
        var cred = await svc.CompleteAttestationAsync("user-webauthn-1", opts, auth.Attestation(opts.Challenge));
        var assertOpts = svc.CreateAssertionOptions([cred]);

        // Authenticator reports a counter lower than what we've already seen → cloned-key signal.
        await Assert.ThrowsAnyAsync<Exception>(() => svc.CompleteAssertionAsync(
            assertOpts, auth.Assertion(assertOpts.Challenge, signCount: 3, userHandle: Handle("user-webauthn-1")), StoredPublicKey(cred),
            storedSignCount: 10, expectedUserId: "user-webauthn-1"));
    }

    /// <summary>
    /// F264 — WebAuthn §7.2 step 6. The ownership callback was hardcoded to true, so an assertion
    /// carrying any user handle at all was accepted for any account. With it implemented, a handle
    /// naming a different user is refused even though the signature, challenge, origin, RP-ID hash and
    /// counter are all valid — this fails on the handle alone.
    /// </summary>
    [Fact]
    public async Task Assertion_WithUserHandleForAnotherAccount_IsRejected()
    {
        var svc = NewService();
        var auth = new VirtualAuthenticator(RpId, Origin);
        var (opts, _) = svc.CreateAttestationOptions(TestUser(), []);
        var cred = await svc.CompleteAttestationAsync("user-webauthn-1", opts, auth.Attestation(opts.Challenge));
        var assertOpts = svc.CreateAssertionOptions([cred]);

        // The authenticator returns the handle it was enrolled with; the server expects a different one.
        var ex = await Assert.ThrowsAsync<Fido2VerificationException>(() => svc.CompleteAssertionAsync(
            assertOpts, auth.Assertion(assertOpts.Challenge, signCount: 1, userHandle: Handle("user-webauthn-1")), StoredPublicKey(cred),
            storedSignCount: 0, expectedUserId: "user-webauthn-2"));
        Assert.Contains("owner", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// F221 — WebAuthn §7.1 step 22. The uniqueness callback was hardcoded to true, so the library's
    /// NonUniqueCredentialId check could never fire and a credential id already in the index could be
    /// registered a second time (a duplicate row with the sign counter reset, sharing one index row).
    /// Now the callback reads the index, and re-registering an id it already holds is refused — whether
    /// the id belongs to another account or to the same one.
    /// </summary>
    [Theory]
    [InlineData("user-webauthn-1")] // same user re-registering — a duplicate row, counter reset
    [InlineData("user-webauthn-9")] // another user's credential id
    public async Task Attestation_WithCredentialIdAlreadyInTheIndex_IsRejected(string existingOwner)
    {
        var store = new InMemoryMfaStore();
        var svc = NewService(store);
        var auth = new VirtualAuthenticator(RpId, Origin);

        Assert.True(await store.TryStoreWebAuthnCredentialIdMappingAsync(auth.CredentialId, existingOwner, "cred-existing"));

        var (opts, _) = svc.CreateAttestationOptions(TestUser(), []);
        var ex = await Assert.ThrowsAsync<Fido2VerificationException>(() => svc.CompleteAttestationAsync(
            "user-webauthn-1", opts, auth.Attestation(opts.Challenge)));
        Assert.Equal(Fido2NetLib.Exceptions.Fido2ErrorCode.NonUniqueCredentialId, ex.Code);
    }

    /// <summary>
    /// F221 — the index claim is the write. Two registrations of the same credential id cannot both
    /// succeed, so the read-then-unconditional-write window that let a racing registration repoint
    /// another account's index row is gone.
    /// </summary>
    [Fact]
    public async Task CredentialIdIndex_IsClaimedOnceAndNotOverwritten()
    {
        var store = new InMemoryMfaStore();
        var credentialId = new byte[] { 1, 2, 3, 4 };

        Assert.True(await store.TryStoreWebAuthnCredentialIdMappingAsync(credentialId, "user-a", "cred-a"));
        Assert.False(await store.TryStoreWebAuthnCredentialIdMappingAsync(credentialId, "user-b", "cred-b"));
        Assert.Equal(("user-a", "cred-a"), await store.FindByWebAuthnCredentialIdAsync(credentialId));
    }
}
