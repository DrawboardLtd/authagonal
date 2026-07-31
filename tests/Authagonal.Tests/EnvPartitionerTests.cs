using Authagonal.Core.Services;

namespace Authagonal.Tests;

// -------------------------------------------------------------------------------------------------
// F275 — the env range must cover every key in the env, not just the ASCII ones.
//
// RangeForEnv is consumed as a half-open PartitionKey filter (pk >= Low && pk < High) by every
// env-scoped read, migration and WIPE across the Azure and SQL providers. The upper bound used to be
// the sentinel "{env}|~", on the reasoning that tilde sorts after any printable ASCII. Keys are not
// ASCII: internationalized email addresses and domains, non-Latin names, IdP-supplied external IDs
// and SAML NameIDs all reach the partition key. Anything sorting at or above '~' fell outside the
// range and was invisible — which under-selects a wipe exactly as readily as a read.
// -------------------------------------------------------------------------------------------------
public sealed class EnvPartitionerTests
{
    private static bool InRange(EnvPartitioner partitioner, string naturalKey)
    {
        var range = partitioner.RangeForEnv();
        Assert.NotNull(range);
        var pk = partitioner.PK(naturalKey);
        return string.CompareOrdinal(pk, range.Value.Low) >= 0
            && string.CompareOrdinal(pk, range.Value.High) < 0;
    }

    [Theory]
    // ASCII, which the old sentinel already covered.
    [InlineData("alice@example.com")]
    [InlineData("0000-user-id")]
    // At or above '~' (0x7E) — every one of these was silently outside the range.
    [InlineData("~tilde-leading-key")]
    [InlineData("émile@example.fr")]
    [InlineData("日本語ユーザー")]
    [InlineData("Ωmega")]
    [InlineData("�replacement")]
    public void EveryKey_IsInsideItsOwnEnvRange(string naturalKey)
    {
        Assert.True(InRange(new EnvPartitioner("sandbox1"), naturalKey),
            $"'{naturalKey}' fell outside its own env range — an env-scoped sweep would skip it");
    }

    /// <summary>
    /// The bound must still be tight: a sibling env sharing the same table set must not be caught by
    /// it. This is the whole point of the prefix, and widening the range naively would break it.
    /// </summary>
    [Theory]
    [InlineData("sandbox2")]
    [InlineData("sandbox1extra")] // shares a prefix up to the delimiter
    [InlineData("live")]
    public void SiblingEnvKeys_AreOutsideTheRange(string otherEnv)
    {
        var range = new EnvPartitioner("sandbox1").RangeForEnv()!.Value;
        var otherPk = new EnvPartitioner(otherEnv).PK("alice@example.com");

        Assert.False(
            string.CompareOrdinal(otherPk, range.Low) >= 0 && string.CompareOrdinal(otherPk, range.High) < 0,
            $"a key belonging to env '{otherEnv}' was caught by sandbox1's range");
    }

    [Fact]
    public void LiveEnv_HasNoRangeFilter()
    {
        Assert.Null(new EnvPartitioner("live").RangeForEnv());
    }

    [Fact]
    public void PkAndStrip_RoundTripANonAsciiKey()
    {
        var partitioner = new EnvPartitioner("sandbox1");
        const string natural = "日本語ユーザー";

        Assert.Equal(natural, partitioner.Strip(partitioner.PK(natural)));
    }
}
