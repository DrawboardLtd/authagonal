using Authagonal.AzureProvider.Stores;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Tests.Infrastructure;
using Azure.Data.Tables;

namespace Authagonal.Tests;

/// <summary>
/// #99, Azure-only: a key that carries the environment prefix, read back into a model, and prefixed again
/// on the way out — so every write after a read landed in a row nobody reads.
/// </summary>
/// <remarks>
/// <c>MfaCredentialEntity.ToModel</c> sets <c>UserId</c> from <c>PartitionKey</c>, and
/// <c>MfaChallengeEntity.ToModel</c> sets <c>ChallengeId</c> from <c>PartitionKey</c>. The stored partition
/// key is <c>EnvPartitioner.PK(natural)</c>, so on a non-live environment the model came back holding
/// <c>env|natural</c>. Feed that back to any write and the store prefixes it again: the row goes to
/// <c>env|env|natural</c>.
/// <para>
/// Invisible on the live environment, where the prefix is empty and <c>PK(x) == x</c> — which is why it
/// survived every test. On dev, staging, or any other named environment the consequences are not cosmetic,
/// and the challenge half is worse than the credential half:
/// </para>
/// <list type="bullet">
/// <item>
/// <b>The MFA attempt cap never bound.</b> The verify handler increments <c>Attempts</c> on the challenge it
/// read and calls <c>StoreChallengeAsync</c>. Doubly prefixed, that increment landed in a phantom, so every
/// read saw the original count and the five-attempt ceiling on a 10^6 code space never fired.
/// </item>
/// <item>
/// <b>Anti-replay never fired either.</b> <c>ConsumeChallengeAsync</c> deleted the phantom and left the real
/// challenge in place, so a challenge stayed redeemable for its whole lifetime after a successful
/// verification.
/// </item>
/// </list>
/// <para>
/// Against Azurite rather than a fake, because the defect is entirely in how a real partition key round
/// trips — an in-memory store keyed on a tuple cannot express it at all.
/// </para>
/// </remarks>
[Collection("Azurite")]
public class MfaEnvPrefixTests(AzuriteFixture azurite)
{
    private readonly TableServiceClient _svc = new(azurite.ConnectionString);

    /// <summary>A store on a NON-live environment, which is the only place the defect is observable.</summary>
    private TableMfaStore NewStore(string prefix)
    {
        TableClient T(string name)
        {
            var c = _svc.GetTableClient($"{prefix}{name}");
            c.CreateIfNotExists();
            return c;
        }

        return new TableMfaStore(
            T("MfaCredentials"), T("MfaChallenges"), T("MfaWebAuthnIndex"), new EnvPartitioner("dev"));
    }

    private static string Prefix() => $"mfaenv{Guid.NewGuid():N}"[..20];

    [Fact]
    public async Task ACredentialReadBackCarriesTheNaturalUserId()
    {
        var store = NewStore(Prefix());
        await store.CreateCredentialAsync(new MfaCredential
        {
            Id = "cred-1", UserId = "user-42", Type = MfaCredentialType.Totp,
            SecretProtected = "secret", CreatedAt = DateTimeOffset.UtcNow,
        });

        var read = await store.GetCredentialAsync("user-42", "cred-1");

        // Not "dev|user-42". Anything that feeds this value back to the store would prefix it a second time.
        Assert.Equal("user-42", read!.UserId);

        // And the same through the list path, which is the one enrolment and verification actually use.
        var all = await store.GetCredentialsAsync("user-42");
        Assert.Equal("user-42", Assert.Single(all).UserId);
    }

    /// <summary>
    /// The round trip that mattered: read a credential, write through it, read it again.
    /// </summary>
    /// <remarks>
    /// With the doubled prefix the update was silently lost — it created a phantom row and the original
    /// stayed exactly as it was, so the caller's next read returned pre-write state with no error anywhere.
    /// </remarks>
    [Fact]
    public async Task AWriteThroughAReadCredentialLandsOnTheRowThatWasRead()
    {
        var store = NewStore(Prefix());
        await store.CreateCredentialAsync(new MfaCredential
        {
            Id = "cred-1", UserId = "user-42", Type = MfaCredentialType.WebAuthn,
            SecretProtected = "secret", CreatedAt = DateTimeOffset.UtcNow,
        });

        var read = await store.GetCredentialAsync("user-42", "cred-1");
        read!.Name = "Renamed";
        await store.UpdateCredentialAsync(read);

        Assert.Equal("Renamed", (await store.GetCredentialAsync("user-42", "cred-1"))!.Name);
    }

    [Fact]
    public async Task AChallengeReadBackCarriesTheNaturalChallengeId()
    {
        var store = NewStore(Prefix());
        await store.StoreChallengeAsync(Challenge("chal-1"));

        var read = await store.GetChallengeAsync("chal-1");

        Assert.Equal("chal-1", read!.ChallengeId);
    }

    /// <summary>
    /// The attempt counter must land on the challenge that was read, or the five-attempt cap is a no-op.
    /// </summary>
    [Fact]
    public async Task IncrementingAttemptsOnAReadChallengePersists()
    {
        var store = NewStore(Prefix());
        await store.StoreChallengeAsync(Challenge("chal-1"));

        // Exactly what the verify handler does on a wrong code.
        var challenge = await store.GetChallengeAsync("chal-1");
        challenge!.Attempts++;
        await store.StoreChallengeAsync(challenge);

        var reread = await store.GetChallengeAsync("chal-1");
        Assert.Equal(1, reread!.Attempts);
    }

    /// <summary>
    /// Consuming a challenge read from the store must actually consume it.
    /// </summary>
    /// <remarks>
    /// This is the anti-replay guarantee. The handler consumes <c>challenge.ChallengeId</c> — the value it
    /// got back from the store — so a doubled prefix deleted a phantom and left the real challenge
    /// redeemable, on every non-live environment.
    /// </remarks>
    [Fact]
    public async Task ConsumingAReadChallengeMakesItUnredeemable()
    {
        var store = NewStore(Prefix());
        await store.StoreChallengeAsync(Challenge("chal-1"));

        var challenge = await store.GetChallengeAsync("chal-1");
        Assert.NotNull(await store.ConsumeChallengeAsync(challenge!.ChallengeId));

        Assert.Null(await store.GetChallengeAsync("chal-1"));
        Assert.Null(await store.ConsumeChallengeAsync("chal-1"));
    }

    private static MfaChallenge Challenge(string id) => new()
    {
        ChallengeId = id,
        UserId = "user-42",
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
    };
    /// <summary>
    /// A challenge is consumable exactly once, even by eight callers at the same moment.
    /// </summary>
    /// <remarks>
    /// <c>ConsumeChallengeAsync</c> read the entity and then issued an UNCONDITIONAL delete. The Azure SDK
    /// documents that overload as "should not fail because the entity does not exist", so every concurrent
    /// caller that got past its own read also got a successful delete and the challenge back — measured at SIX
    /// of eight before the fix. A single-use MFA challenge that six callers can spend is not single-use: it is
    /// the anti-replay guard on the second factor.
    /// <para>
    /// The SQL and DynamoDB stores were already atomic (both use a delete-if-exists returning the row, the
    /// Dynamo one under a condition expression) and the SQL suite already asserted exactly this. The Azure
    /// store is the odd one out, again, so the assertion is now made against all three.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AChallengeIsConsumedExactlyOnce_UnderConcurrency()
    {
        var store = NewStore(Prefix());

        await store.StoreChallengeAsync(new MfaChallenge
        {
            ChallengeId = "ch-1",
            UserId = "u1",
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
        });
        Assert.NotNull(await store.GetChallengeAsync("ch-1"));

        var consumed = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => store.ConsumeChallengeAsync("ch-1")));

        Assert.Equal(1, consumed.Count(c => c is not null));
        Assert.Null(await store.GetChallengeAsync("ch-1"));
    }
}
