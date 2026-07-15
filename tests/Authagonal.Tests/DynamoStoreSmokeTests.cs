using Amazon.DynamoDBv2;
using Authagonal.AwsProvider.Dynamo;
using Authagonal.AwsProvider.Stores;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace Authagonal.Tests;

/// <summary>
/// Behavioral smoke over the non-user Dynamo stores against real DynamoDB semantics: the grant
/// store's atomic single-use consume, the MFA challenge round-trip (incl. the Attempts retry
/// budget), the SAML replay cache's single-use request validation, and change-log capture.
/// </summary>
[Collection("Dynamo")]
public class DynamoStoreSmokeTests(DynamoFixture dynamo)
{
    private readonly IAmazonDynamoDB _db = dynamo.CreateClient();

    private async Task<DynamoTable> T(string name)
    {
        await DynamoTableProvisioner.EnsureTableAsync(_db, name);
        return new DynamoTable(_db, name);
    }

    [Fact]
    public async Task GrantStore_RoundTrip_AndAtomicSingleUseConsume()
    {
        var store = new DynamoGrantStore(
            await T("sgGrants"), await T("sgBySubject"), await T("sgByExpiry"),
            EnvPartitioner.Live, NullLogger<DynamoGrantStore>.Instance);

        var grant = new PersistedGrant
        {
            Key = "authcode-1",
            Type = "authorization_code",
            ClientId = "client-a",
            SubjectId = "u1",
            Data = "{\"sub\":\"u1\"}",
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
        };
        await store.StoreAsync(grant);

        var read = await store.GetAsync("authcode-1");
        Assert.Equal("client-a", read?.ClientId);
        Assert.Single(await store.GetBySubjectAsync("u1"));

        // Exactly one concurrent consumer may win.
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => store.TryConsumeAsync("authcode-1")));
        Assert.Equal(1, results.Count(won => won));
        Assert.Empty(await store.GetBySubjectAsync("u1")); // consumed grants drop out of the subject view

        await store.StoreAsync(new PersistedGrant
        {
            Key = "refresh-1",
            Type = "refresh_token",
            ClientId = "client-a",
            SubjectId = "u1",
            Data = "{}",
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        });
        await store.RemoveAllBySubjectAsync("u1");
        Assert.Null(await store.GetAsync("refresh-1"));
        Assert.Empty(await store.GetBySubjectAsync("u1"));
    }

    [Fact]
    public async Task MfaStore_ChallengeAttempts_RoundTrip_AndSingleConsume()
    {
        var store = new DynamoMfaStore(
            await T("smCreds"), await T("smChallenges"), await T("smWebAuthn"), EnvPartitioner.Live);

        var challenge = new MfaChallenge
        {
            ChallengeId = "ch-1",
            UserId = "u1",
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            Attempts = 0,
        };
        await store.StoreChallengeAsync(challenge);

        // The validate-before-consume flow: peek, bump Attempts, re-store, then consume on success.
        var peeked = await store.GetChallengeAsync("ch-1");
        Assert.NotNull(peeked);
        peeked!.Attempts++;
        await store.StoreChallengeAsync(peeked);
        Assert.Equal(1, (await store.GetChallengeAsync("ch-1"))?.Attempts);

        var consumed = await store.ConsumeChallengeAsync("ch-1");
        Assert.Equal("u1", consumed?.UserId);
        Assert.Null(await store.ConsumeChallengeAsync("ch-1")); // single-use
    }

    [Fact]
    public async Task SamlReplayCache_RequestIds_AreSingleUse_AndCarryReturnUrl()
    {
        var cache = new DynamoSamlReplayCache(await T("srReplay"), TimeSpan.FromMinutes(10));

        await cache.StoreRequestIdAsync("req-1", "conn-a");
        Assert.Equal("conn-a", await cache.ValidateAndConsumeAsync("req-1"));
        Assert.Null(await cache.ValidateAndConsumeAsync("req-1")); // replay rejected

        await cache.StoreRequestAsync("req-2", "conn-b", "/portal/settings");
        var state = await cache.ValidateAndConsumeRequestAsync("req-2");
        Assert.Equal("conn-b", state?.ConnectionId);
        Assert.Equal("/portal/settings", state?.ReturnUrl);
        Assert.Null(await cache.ValidateAndConsumeRequestAsync("req-2"));
    }

    [Fact]
    public async Task ChangeWriter_CapturesUpsertsAndDeletes()
    {
        var table = await T("scTombstones");
        var writer = new DynamoChangeWriter(table);

        await writer.WriteUpsertAsync("Users", "u1", "profile");
        await writer.WriteAsync("Users", "u2", "profile");
        await writer.WriteUpsertBatchAsync("UserEmails", [("E1", "lookup"), ("E2", "lookup")]);

        static string S(Dictionary<string, Amazon.DynamoDBv2.Model.AttributeValue> item, string name)
            => item.TryGetValue(name, out var v) ? v.S ?? "" : "";

        var ops = new Dictionary<string, string>();
        await foreach (var item in table.ScanAsync())
            ops[$"{S(item, "pk")}|{S(item, "sk")}"] = S(item, "op");

        Assert.Equal("U", ops["Users|u1|profile"]);
        Assert.Equal("D", ops["Users|u2|profile"]);
        Assert.Equal("U", ops["UserEmails|E1|lookup"]);
        Assert.Equal(4, ops.Count);
    }
}
