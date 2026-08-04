using Authagonal.Core.Models;
using Authagonal.Protocol.Services;
using Authagonal.Server.Services;
using Authagonal.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Authagonal.Tests;

/// <summary>
/// Signing-key rotation decision logic. Rotation is two-phased: <see cref="ProtocolSigningKeyOps.CheckAndRotateAsync"/>
/// deactivates a key approaching expiry (returns true so the caller force-refreshes), and the key
/// manager's refresh path (<see cref="ProtocolSigningKeyOps.EnsureActiveKeyAsync"/>) mints the
/// replacement. The old key stays in storage until it actually expires, so
/// <see cref="ProtocolSigningKeyOps.BuildJwksAsync"/> keeps publishing it — tokens signed
/// pre-rotation stay verifiable (publish-ahead overlap).
/// </summary>
public class SigningKeyRotationTests
{
    private const int LifetimeDays = 90;
    private const int LeadTimeDays = 14;

    private readonly InMemorySigningKeyStore _store = new();

    /// <summary>Real ES256 key whose age is driven by back-dating CreatedAt (no clock seam in the ops class).</summary>
    private static SigningKeyInfo KeyCreatedDaysAgo(double daysAgo) =>
        ProtocolSigningKeyOps.GenerateNewKey(DateTimeOffset.UtcNow.AddDays(-daysAgo), LifetimeDays);

    private Task<bool> CheckAndRotateAsync() =>
        ProtocolSigningKeyOps.CheckAndRotateAsync(_store, LifetimeDays, LeadTimeDays, NullLogger.Instance);

    // Both of these take the SAME lead time CheckAndRotateAsync above uses, because that is what the
    // Authagonal.Server host does (AuthagonalExtensions mirrors Auth:KeyRotationLeadTimeDays into
    // AuthagonalProtocolOptions). Calling them without it is what let the publish-ahead defect survive a
    // suite that appeared to cover it: with the lead time defaulted to 0 the publish window is one day
    // against expiry, and rotation retires the key thirteen days before that window opens.
    private Task<SigningKeyInfo> EnsureActiveKeyAsync() =>
        ProtocolSigningKeyOps.EnsureActiveKeyAsync(
            _store, LifetimeDays, NullLogger.Instance, rotationLeadTimeDays: LeadTimeDays);

    private Task<bool> PublishSuccessorIfDueAsync() =>
        ProtocolSigningKeyOps.PublishSuccessorIfDueAsync(
            _store, LifetimeDays, NullLogger.Instance, rotationLeadTimeDays: LeadTimeDays);

    // ── No-rotation cases ────────────────────────────────────────────

    [Fact]
    public async Task FreshKey_OutsideLeadTime_DoesNotRotate()
    {
        var key = KeyCreatedDaysAgo(0); // expires in 90 days, threshold 14
        await _store.StoreAsync(key);

        Assert.False(await CheckAndRotateAsync());

        var active = await _store.GetActiveKeyAsync();
        Assert.NotNull(active);
        Assert.Equal(key.KeyId, active!.KeyId);
        Assert.True(active.IsActive);
    }

    [Fact]
    public async Task NoActiveKey_ReturnsFalse_WithoutGenerating()
    {
        Assert.False(await CheckAndRotateAsync());
        Assert.Empty(await _store.GetAllAsync());
    }

    // ── Rotation inside the lead time ────────────────────────────────

    [Fact]
    public async Task KeyInsideLeadTime_Rotates_AndDeactivatesOldKey()
    {
        var old = KeyCreatedDaysAgo(85); // expires in ~5 days < 14-day lead
        await _store.StoreAsync(old);

        Assert.True(await CheckAndRotateAsync());

        // Old key deactivated but NOT deleted — it must survive for verification overlap.
        var all = await _store.GetAllAsync();
        var stored = Assert.Single(all);
        Assert.Equal(old.KeyId, stored.KeyId);
        Assert.False(stored.IsActive);
        Assert.Null(await _store.GetActiveKeyAsync());
    }

    [Fact]
    public async Task Rotation_PublishAhead_NewKeyActive_OldKeyStillInJwks()
    {
        var old = KeyCreatedDaysAgo(85);
        await _store.StoreAsync(old);

        // The rotation service's real sequence: rotate, then the key manager refresh
        // (which funnels through EnsureActiveKeyAsync) mints the replacement.
        Assert.True(await CheckAndRotateAsync());
        var fresh = await EnsureActiveKeyAsync();

        Assert.NotEqual(old.KeyId, fresh.KeyId);
        Assert.True(fresh.IsActive);
        Assert.Equal(fresh.KeyId, (await _store.GetActiveKeyAsync())!.KeyId);

        // JWKS publishes both: the new signer AND the not-yet-expired old key, so
        // tokens signed before rotation still verify.
        var jwks = await ProtocolSigningKeyOps.BuildJwksAsync(_store);
        Assert.Equal(2, jwks.Count);
        Assert.Contains(jwks, k => k.Kid == old.KeyId);
        Assert.Contains(jwks, k => k.Kid == fresh.KeyId);
    }

    // ── Publish-ahead ────────────────────────────────────────────────

    /// <summary>
    /// The successor is published, inactive, before it is ever used to sign.
    /// </summary>
    /// <remarks>
    /// <c>EnsureActiveKeyAsync</c> deactivated the old key and generated the replacement in the same breath,
    /// and <c>BuildSigningCredentials</c> made that brand-new key the signer immediately — so a <c>kid</c> first
    /// appeared in JWKS at the exact instant it started signing. Peers cache keys for
    /// <c>SigningKeyCacheRefreshMinutes</c> and both JWKS endpoints send <c>max-age=3600</c>, so every token
    /// minted under the new key was rejected by peer nodes and by any shared cache until their TTL lapsed.
    /// <para>
    /// Both endpoints' comments assert the opposite — "the next key is published days ahead of use, so a short
    /// shared cache is always safe" — and the test named for publish-ahead did not test it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task SuccessorIsPublishedBeforeItSigns()
    {
        // Inside the publish-ahead window — the rotation lead time plus a day's margin.
        var active = KeyCreatedDaysAgo(LifetimeDays - 1);
        await _store.StoreAsync(active);

        Assert.True(await PublishSuccessorIfDueAsync());

        // Two keys: the signer, and a successor that is published but NOT active.
        var all = await _store.GetAllAsync();
        Assert.Equal(2, all.Count);
        Assert.Equal(active.KeyId, (await _store.GetActiveKeyAsync())!.KeyId);

        var successor = Assert.Single(all, k => k.KeyId != active.KeyId);
        Assert.False(successor.IsActive);

        // Published in JWKS while unused, which is the whole point: verifiers see it before any token carries it.
        var jwks = await ProtocolSigningKeyOps.BuildJwksAsync(_store);
        Assert.Contains(jwks, k => k.Kid == successor.KeyId);
        Assert.Contains(jwks, k => k.Kid == active.KeyId);
    }

    /// <summary>Publishing is idempotent — a cluster publishes one successor, not one per refresh.</summary>
    [Fact]
    public async Task PublishingIsIdempotent()
    {
        await _store.StoreAsync(KeyCreatedDaysAgo(LifetimeDays - 1));

        Assert.True(await PublishSuccessorIfDueAsync());
        Assert.False(await PublishSuccessorIfDueAsync());

        Assert.Equal(2, (await _store.GetAllAsync()).Count);
    }

    /// <summary>Nothing is published while the active key is nowhere near expiry.</summary>
    /// <remarks>
    /// The control for the window: publishing on every refresh regardless of expiry would fill JWKS with keys
    /// and defeat the point of a bounded key set.
    /// </remarks>
    [Fact]
    public async Task NothingIsPublishedForAFreshKey()
    {
        await _store.StoreAsync(KeyCreatedDaysAgo(1));

        Assert.False(await PublishSuccessorIfDueAsync());
        Assert.Single(await _store.GetAllAsync());
    }

    /// <summary>
    /// The published successor is what gets promoted — no new key is minted at rotation.
    /// </summary>
    /// <remarks>
    /// The property that makes the cache header true: by the time this key signs, every verifier has already
    /// had it in their key set.
    /// </remarks>
    [Fact]
    public async Task ThePublishedSuccessorIsPromotedRatherThanANewKeyMinted()
    {
        var active = KeyCreatedDaysAgo(LifetimeDays - 1);
        await _store.StoreAsync(active);
        await PublishSuccessorIfDueAsync();

        var published = Assert.Single(await _store.GetAllAsync(), k => k.KeyId != active.KeyId);

        // Rotation retires the active key; the next refresh settles on a signer.
        Assert.True(await CheckAndRotateAsync());
        var signer = await EnsureActiveKeyAsync();

        // The already-published key, not a third one.
        Assert.Equal(published.KeyId, signer.KeyId);
        Assert.True(signer.IsActive);
        Assert.Equal(2, (await _store.GetAllAsync()).Count);
    }

    /// <summary>
    /// A RETIRED key is never promoted back, even though it is also inactive and unexpired.
    /// </summary>
    /// <remarks>
    /// The bug the first version of this change had: the successor search looked only at <c>IsActive</c>, so
    /// straight after rotation deactivated the outgoing key it found that key and promoted it again. A retired
    /// key is within the rotation lead time of expiry; a successor has a full lifetime. The suite caught it.
    /// </remarks>
    [Fact]
    public async Task ARetiredKeyIsNotPromotedBack()
    {
        var old = KeyCreatedDaysAgo(85); // ~5 days left, inside the 14-day lead time
        await _store.StoreAsync(old);

        Assert.True(await CheckAndRotateAsync());
        var signer = await EnsureActiveKeyAsync();

        Assert.NotEqual(old.KeyId, signer.KeyId);
        Assert.True(signer.IsActive);
    }

    /// <summary>
    /// With rotation enabled at the production lead time, the successor is published BEFORE rotation can
    /// retire the active key — so rotation promotes it instead of minting a signer nobody has seen.
    /// </summary>
    /// <remarks>
    /// The regression test for the defect that made publish-ahead dead code for every deployment with
    /// <c>Auth:KeyRotationEnabled</c> on. The publish window was measured against EXPIRY (one day), while
    /// <see cref="ProtocolSigningKeyOps.CheckAndRotateAsync"/> retires the key at
    /// <c>KeyRotationLeadTimeDays</c> — 14 days by default. The key was therefore deactivated thirteen days
    /// before the publish window opened; <c>GetActiveKeyAsync</c> then returned null, publish-ahead bailed
    /// out on its own "no active key is EnsureActiveKeyAsync's job" guard, and the retired key failed the
    /// successor floor — so a brand-new key was minted and signed in the same call.
    /// <para>
    /// This drives the real sequence at the real lead time. Before the fix it failed on the final assertion:
    /// the signer was a third key, not the published successor.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task PublishAhead_AtProductionLeadTime_SuccessorIsPublishedBeforeRotationRetiresTheKey()
    {
        // Just outside the rotation lead time, so rotation has NOT fired yet, but inside the publish
        // window (lead time + a day). This is the state every deployment passes through once per key.
        var active = KeyCreatedDaysAgo(LifetimeDays - LeadTimeDays - 0.5);
        await _store.StoreAsync(active);

        Assert.False(await CheckAndRotateAsync()); // not yet due for retirement
        Assert.True(await PublishSuccessorIfDueAsync()); // but the successor goes out now

        var published = Assert.Single(await _store.GetAllAsync(), k => k.KeyId != active.KeyId);
        Assert.False(published.IsActive);

        // Every verifier can see it while it is still unused — the property both JWKS endpoints'
        // `max-age=3600` headers assert.
        var jwksBeforeUse = await ProtocolSigningKeyOps.BuildJwksAsync(_store);
        Assert.Contains(jwksBeforeUse, k => k.Kid == published.KeyId);

        // Now let rotation run. It must promote what was published, not mint a third key.
        await _store.StoreAsync(new SigningKeyInfo
        {
            KeyId = active.KeyId,
            Algorithm = active.Algorithm,
            KeyMaterialJson = active.KeyMaterialJson,
            IsActive = true,
            CreatedAt = active.CreatedAt,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(LeadTimeDays - 1), // now inside the lead time
        });

        Assert.True(await CheckAndRotateAsync());
        var signer = await EnsureActiveKeyAsync();

        Assert.Equal(published.KeyId, signer.KeyId);
        Assert.True(signer.IsActive);
        Assert.Equal(2, (await _store.GetAllAsync()).Count); // no third key
    }

    /// <summary>
    /// A node that can never hold the generation lease publishes nothing — and says so.
    /// </summary>
    /// <remarks>
    /// <c>NeverLeaseProvider</c> is installed container-wide by <c>Cluster:RunLeaderElection=false</c> and by
    /// each <c>Use*Bus</c> helper, so a whole Deployment can be in this state. Publishing must not proceed
    /// (two nodes publishing two successors is the race the lease exists to prevent), which makes the log
    /// line the only signal that the control is off.
    /// </remarks>
    [Fact]
    public async Task PublishAhead_WithALeaseThatNeverGrants_PublishesNothing()
    {
        await _store.StoreAsync(KeyCreatedDaysAgo(LifetimeDays - 1));

        Assert.False(await ProtocolSigningKeyOps.PublishSuccessorIfDueAsync(
            _store, LifetimeDays, NullLogger.Instance,
            lease: new Authagonal.Core.Clustering.NeverLeaseProvider(), nodeId: "node-1",
            rotationLeadTimeDays: LeadTimeDays));

        Assert.Single(await _store.GetAllAsync());
    }

    /// <summary>
    /// A lead time too large for the key lifetime disables publish-ahead rather than inverting it.
    /// </summary>
    /// <remarks>
    /// The floor that tells a published successor from a retired key sits between "at most the lead time
    /// remaining" and "a full lifetime remaining". When the lead time exceeds half the lifetime there is no
    /// such gap, and the old <c>lifetime/2</c> rule silently started promoting the key rotation had just
    /// retired. It is refused loudly instead — and, critically, the retired key still is not promoted.
    /// </remarks>
    [Fact]
    public async Task PublishAhead_LeadTimeTooLargeForLifetime_IsRefused_AndStillWillNotPromoteARetiredKey()
    {
        var old = KeyCreatedDaysAgo(LifetimeDays - 50); // 50 days left, lead time 60 → already retirable
        await _store.StoreAsync(old);

        Assert.False(await ProtocolSigningKeyOps.PublishSuccessorIfDueAsync(
            _store, LifetimeDays, NullLogger.Instance, rotationLeadTimeDays: 60));

        Assert.True(await ProtocolSigningKeyOps.CheckAndRotateAsync(
            _store, LifetimeDays, 60, NullLogger.Instance));

        var signer = await ProtocolSigningKeyOps.EnsureActiveKeyAsync(
            _store, LifetimeDays, NullLogger.Instance, rotationLeadTimeDays: 60);

        Assert.NotEqual(old.KeyId, signer.KeyId);
        Assert.True(signer.IsActive);
    }

    // ── Idempotence ──────────────────────────────────────────────────

    [Fact]
    public async Task CheckTwice_WithoutRefreshBetween_SecondCallIsNoOp()
    {
        await _store.StoreAsync(KeyCreatedDaysAgo(85));

        Assert.True(await CheckAndRotateAsync());
        // Active key is now deactivated; a second sweep before the key manager refreshed
        // must not throw or double-rotate.
        Assert.False(await CheckAndRotateAsync());
        Assert.Single(await _store.GetAllAsync());
    }

    [Fact]
    public async Task AfterFullRotation_SecondCheck_DoesNotRotateFreshKey()
    {
        await _store.StoreAsync(KeyCreatedDaysAgo(85));
        Assert.True(await CheckAndRotateAsync());
        var fresh = await EnsureActiveKeyAsync();

        Assert.False(await CheckAndRotateAsync());

        Assert.Equal(fresh.KeyId, (await _store.GetActiveKeyAsync())!.KeyId);
        Assert.Equal(2, (await _store.GetAllAsync()).Count); // no third key minted
    }

    // ── EnsureActiveKeyAsync generation cases ────────────────────────

    [Fact]
    public async Task EnsureActiveKey_EmptyStore_GeneratesActiveEs256Key()
    {
        var key = await EnsureActiveKeyAsync();

        Assert.True(key.IsActive);
        Assert.Equal(ProtocolSigningKeyOps.Algorithm, key.Algorithm);
        Assert.True(key.ExpiresAt > DateTimeOffset.UtcNow.AddDays(LifetimeDays - 1));
        Assert.Equal(key.KeyId, (await _store.GetActiveKeyAsync())!.KeyId);
    }

    [Fact]
    public async Task EnsureActiveKey_UnsupportedLegacyAlgorithm_IsReplaced()
    {
        var legacyRsa = new SigningKeyInfo
        {
            KeyId = "legacy-rsa",
            Algorithm = "RS256",
            KeyMaterialJson = "{}",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30), // not expired — replaced purely for the algorithm
        };
        await _store.StoreAsync(legacyRsa);

        var key = await EnsureActiveKeyAsync();

        Assert.NotEqual("legacy-rsa", key.KeyId);
        Assert.Equal(ProtocolSigningKeyOps.Algorithm, key.Algorithm);
        var all = await _store.GetAllAsync();
        Assert.False(all.Single(k => k.KeyId == "legacy-rsa").IsActive);
    }

    // ── SigningKeyRotationService (host wrapper) ─────────────────────

    [Fact]
    public async Task RotationService_Disabled_CompletesWithoutTouchingCollaborators()
    {
        // KeyRotationEnabled defaults to false; the service must short-circuit before ever
        // dereferencing the leader election, scope factory, or key manager (nulls prove it —
        // any touch would fault ExecuteTask).
        var service = new SigningKeyRotationService(
            scopeFactory: null!,
            leaderService: null!,
            keyManager: null!,
            Options.Create(new AuthOptions { KeyRotationEnabled = false }),
            NullLogger<SigningKeyRotationService>.Instance);

        await service.StartAsync(CancellationToken.None);

        // ExecuteAsync may start deferred rather than synchronously; if the disabled guard were
        // missing it would either hang on the interval delay (→ timeout) or NRE on a null
        // collaborator (→ the await throws). Completing cleanly within the window proves the
        // short-circuit.
        Assert.NotNull(service.ExecuteTask);
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(service.ExecuteTask.IsCompletedSuccessfully);
    }
}
