using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.Protocol.Services;
using Authagonal.Server;
using Authagonal.Server.Services;
using Authagonal.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Authagonal.Tests;

/// <summary>
/// A stored hash is an instruction to this server about how much CPU to spend on the next ANONYMOUS
/// <c>/connect/token</c> call for that client. Every lever in that instruction has to be bounded, and the
/// bcrypt path had none.
/// </summary>
/// <remarks>
/// Both bcrypt verifiers recognised a hash on a four-character prefix test and caught only
/// <c>SaltParseException</c>, which admitted two stored denial-of-service shapes:
/// <list type="bullet">
/// <item>an unbounded cost factor — cost is a base-2 exponent and the library accepts up to 31, so
/// <c>$2a$31$…</c> is 2^31 key expansions, pinning a thread-pool thread for days per request;</item>
/// <item>a malformed body, which throws <c>IndexOutOfRangeException</c>, <c>FormatException</c> or
/// <c>ArgumentOutOfRangeException</c> instead — none of them caught, so every authentication for that
/// principal answered 500 forever.</item>
/// </list>
/// <para>
/// There are two verifiers because <c>BCryptClientSecretVerifier</c> ships in <c>Authagonal.Protocol</c> and
/// <c>PasswordHasher</c> is the Server host's, so both are asserted here against the one shared rule.
/// </para>
/// </remarks>
public sealed class SecretHashCostBoundTests
{
    private const string CostBomb = "$2a$31$abcdefghijklmnopqrstuuXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX";

    /// <summary>The malformed shapes, each with the exception the pinned library actually throws for it.</summary>
    public static TheoryData<string, string> Malformed => new()
    {
        { "$2a$1", "IndexOutOfRangeException" },
        { "$2b$", "IndexOutOfRangeException" },
        { "$2a$xy$abcdefghijklmnopqrstuuXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX", "FormatException" },
        { "$2a$11$short", "ArgumentOutOfRangeException" },
        { "$2y$11$aaaaaaaaaa", "ArgumentOutOfRangeException" },
    };

    // ── the shared rule ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ARealBcryptHashIsValid()
    {
        Assert.True(BcryptHashFormat.IsValid(BCrypt.Net.BCrypt.HashPassword("x", 10)));
        Assert.True(BcryptHashFormat.IsValid(BCrypt.Net.BCrypt.HashPassword("x", BcryptHashFormat.MaxCost)));
    }

    [Fact]
    public void AboveTheCostBoundIsNotValid()
    {
        Assert.False(BcryptHashFormat.IsValid(CostBomb));

        // One step over is refused, so the bound is the bound rather than a rough filter.
        Assert.False(BcryptHashFormat.IsValid(
            BCrypt.Net.BCrypt.HashPassword("x", BcryptHashFormat.MaxCost + 1)));
    }

    [Theory]
    [MemberData(nameof(Malformed))]
    public void AMalformedBcryptHashIsNotValid(string hash, string throwsInstead)
    {
        Assert.False(BcryptHashFormat.IsValid(hash), $"admitting this one produced {throwsInstead}");

        // Still recognised as BCRYPT's problem, so it is refused there rather than falling through to the
        // unprefixed ASP.NET Identity branch, where the cost parameters come from the blob itself.
        Assert.True(BcryptHashFormat.HasBcryptPrefix(hash));
    }

    // ── neither verifier throws, and neither burns the CPU ───────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Malformed))]
    public void TheServerHostVerifierFailsClosedOnAMalformedHash(string hash, string throwsInstead)
    {
        _ = throwsInstead;
        Assert.Equal(PasswordVerifyResult.Failed,
            CheapHasher.Password().VerifyPassword("anything", hash));
    }

    [Fact]
    public void TheServerHostVerifierRefusesACostBombWithoutComputingIt()
    {
        // If the bound were absent this call would not return for days; the assertion is that it returns.
        Assert.Equal(PasswordVerifyResult.Failed,
            CheapHasher.Password().VerifyPassword("anything", CostBomb));
    }

    [Theory]
    [MemberData(nameof(Malformed))]
    public async Task TheProtocolVerifierFailsClosedOnAMalformedHash(string hash, string throwsInstead)
    {
        _ = throwsInstead;
        var client = new OAuthClient { ClientId = "c", ClientSecretHashes = [hash] };

        Assert.False(await ProtocolVerifier().VerifyAsync(client, "anything"));
    }

    [Fact]
    public async Task TheProtocolVerifierRefusesACostBombWithoutComputingIt()
    {
        var client = new OAuthClient { ClientId = "c", ClientSecretHashes = [CostBomb] };

        Assert.False(await ProtocolVerifier().VerifyAsync(client, "anything"));
    }

    /// <summary>
    /// The control: the Protocol verifier still authenticates a legitimate bcrypt secret.
    /// </summary>
    /// <remarks>
    /// Without this, a verifier that refused everything would satisfy every assertion above while breaking
    /// every client whose secret was seeded through <c>OidcClientDescriptor</c>, which bcrypt-hashes on seed.
    /// </remarks>
    [Fact]
    public async Task TheProtocolVerifierStillAcceptsALegitimateSecret()
    {
        var client = new OAuthClient
        {
            ClientId = "c",
            ClientSecretHashes = [BCrypt.Net.BCrypt.HashPassword("the-secret", 10)],
        };

        Assert.True(await ProtocolVerifier().VerifyAsync(client, "the-secret"));
        Assert.False(await ProtocolVerifier().VerifyAsync(client, "wrong"));
    }

    // Internal to Authagonal.Protocol, which grants InternalsVisibleTo to this project.
    private static IClientSecretVerifier ProtocolVerifier() => new BCryptClientSecretVerifier();

    // ── the iteration ceiling ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The write path cannot be configured past what the verify path accepts.
    /// </summary>
    /// <remarks>
    /// Only a floor was validated, so <c>Auth:Pbkdf2Iterations = 1000001</c> started a healthy host that then
    /// wrote hashes it refuses on read — silently, and irreversibly, because the cost is recorded in each
    /// stored blob. This asserts the two numbers are the same number.
    /// </remarks>
    [Fact]
    public void TheConfiguredCeilingIsTheVerifiableCeiling()
    {
        var atTheCeiling = new PasswordHasher(Options.Create(new AuthOptions
        {
            Pbkdf2Iterations = AuthOptions.MaximumPbkdf2Iterations,
        }));

        var hash = atTheCeiling.HashPassword("x");

        // The hash written at the maximum permitted cost verifies. One above would not, which is what the
        // startup validator now refuses to let an operator configure.
        Assert.Equal(PasswordVerifyResult.Success, atTheCeiling.VerifyPassword("x", hash));
        Assert.True(AuthOptions.MaximumPbkdf2Iterations > AuthOptions.MinimumPbkdf2Iterations);
    }

    /// <summary>
    /// The registered startup validators refuse a configured cost outside the verifiable range.
    /// </summary>
    /// <remarks>
    /// Asserted against the validators <c>AddAuthagonalCore</c> actually registers, not a restated predicate —
    /// a copy of the rule would pass whether or not the host enforces it. Note the test factory deliberately
    /// REMOVES these (the suite runs at 1,000 iterations for speed), which is why they need covering here.
    /// </remarks>
    [Theory]
    [InlineData(1_000, false)]                                     // below the floor
    [InlineData(AuthOptions.MinimumPbkdf2Iterations, true)]
    [InlineData(600_000, true)]                                    // the shipped default
    [InlineData(AuthOptions.MaximumPbkdf2Iterations, true)]
    [InlineData(AuthOptions.MaximumPbkdf2Iterations + 1, false)]   // writes hashes it cannot verify
    [InlineData(6_000_000, false)]                                 // the documented fat-finger: 600000 → 6000000
    public void StartupValidationBoundsTheConfiguredCostBothWays(int iterations, bool expectValid)
    {
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(configuration);
        services.AddAuthagonalCore(configuration);

        var validators = services.BuildServiceProvider()
            .GetServices<IValidateOptions<AuthOptions>>()
            .ToList();
        Assert.NotEmpty(validators);

        var options = new AuthOptions { Pbkdf2Iterations = iterations };
        var failed = validators
            .Select(v => v.Validate(Microsoft.Extensions.Options.Options.DefaultName, options))
            .Any(r => r.Failed);

        Assert.Equal(expectValid, !failed);
    }

    /// <summary>
    /// A hash recorded ABOVE the ceiling still fails closed, since the bound also protects against a crafted
    /// imported blob.
    /// </summary>
    [Fact]
    public void AHashRecordingMoreThanTheCeilingIsRefused()
    {
        var overTheCeiling = new PasswordHasher(Options.Create(new AuthOptions
        {
            Pbkdf2Iterations = AuthOptions.MaximumPbkdf2Iterations + 1,
        }));

        // Deriving at that cost is slow but bounded; the point is what happens on the way back.
        var hash = overTheCeiling.HashPassword("x");

        Assert.Equal(PasswordVerifyResult.Failed, overTheCeiling.VerifyPassword("x", hash));
    }

    // ── the upgrade is a conditional write ──────────────────────────────────────────────────────────

    /// <summary>
    /// A secret rotation landing during a legacy-hash upgrade wins.
    /// </summary>
    /// <remarks>
    /// The upgrade used to re-read the record, mutate the hash list, and write the WHOLE record back
    /// unconditionally. A rotation landing between the read and the write was reverted, so the compromised
    /// secret kept working while the audit log recorded a successful rotation — and the attacker did not need
    /// to observe the rotation, because the throttle permits 30 authentications a minute per client.
    /// </remarks>
    [Fact]
    public async Task AConcurrentRotationIsNotRevertedByTheUpgrade()
    {
        var store = new InMemoryClientStore();
        var legacy = "SHA256$" + Convert.ToBase64String(
            System.Security.Cryptography.SHA256.HashData("old-secret"u8.ToArray()));

        await store.UpsertAsync(new OAuthClient
        {
            ClientId = "c",
            ClientSecretHashes = [legacy],
        });

        // The rotation lands first: the entry the upgrade expects is gone.
        var rotated = CheapHasher.Password().HashPassword("new-secret");
        await store.UpsertAsync(new OAuthClient { ClientId = "c", ClientSecretHashes = [rotated] });

        var applied = await ((IClientStore)store).TryUpgradeSecretHashAsync(
            "c", 0, legacy, CheapHasher.Password().HashPassword("old-secret"));

        Assert.False(applied);

        var stored = Assert.Single((await store.GetAsync("c"))!.ClientSecretHashes);
        Assert.Equal(rotated, stored);
    }

    /// <summary>
    /// The control: with nothing racing, the upgrade applies.
    /// </summary>
    [Fact]
    public async Task WithNoRaceTheUpgradeApplies()
    {
        var store = new InMemoryClientStore();
        var legacy = "SHA256$" + Convert.ToBase64String(
            System.Security.Cryptography.SHA256.HashData("s"u8.ToArray()));

        await store.UpsertAsync(new OAuthClient { ClientId = "c", ClientSecretHashes = [legacy] });

        var upgraded = CheapHasher.Password().HashPassword("s");
        Assert.True(await ((IClientStore)store).TryUpgradeSecretHashAsync("c", 0, legacy, upgraded));
        Assert.Equal(upgraded, Assert.Single((await store.GetAsync("c"))!.ClientSecretHashes));
    }
}
