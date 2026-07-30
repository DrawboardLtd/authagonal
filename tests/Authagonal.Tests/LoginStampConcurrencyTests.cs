using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.AzureProvider.Entities;
using Authagonal.AzureProvider.Stores;
using Authagonal.Tests.Infrastructure;
using Azure.Data.Tables;

namespace Authagonal.Tests;

/// <summary>
/// <c>RecordSuccessfulLoginAsync</c> read the user entity and wrote it back with an unconditional
/// full-entity Replace, so any administrative write that landed in between was silently reverted. The
/// reverted set was not just the login columns — it included IsActive (SCIM deprovision), MfaEnabled
/// (enrolment, which login gates on), RolesJson (role revocation), PasswordHash and SecurityStamp. An
/// attacker who keeps authenticating controls one side of the race. Verified against real Azure Table
/// ETag semantics via Azurite.
/// </summary>
[Collection("Azurite")]
public class LoginStampConcurrencyTests(AzuriteFixture azurite)
{
    private readonly TableServiceClient _svc = new(azurite.ConnectionString);

    private (TableUserStore Store, TableClient Users) NewStore(string prefix)
    {
        TableClient T(string name)
        {
            var c = _svc.GetTableClient($"{prefix}{name}");
            c.CreateIfNotExists();
            return c;
        }
        var users = T("Users");
        var store = new TableUserStore(users, T("Emails"), T("Logins"), T("ExtIds"), null, null,
            EnvPartitioner.Live);
        return (store, users);
    }

    private static AuthUser User(string id) => new()
    {
        Id = id,
        Email = $"{id}@example.com",
        NormalizedEmail = $"{id}@example.com".ToUpperInvariant(),
        IsActive = true,
        PasswordHash = "hash-v1",
        SecurityStamp = "stamp-v1",
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>
    /// The race needs the login stamp's READ to land before the admin write and its WRITE after. There is
    /// no seam to pause the store mid-call, so this drives genuine concurrency: a burst of login stamps
    /// runs while an admin deprovisions the account partway through. Any stamp that read the pre-admin row
    /// and wrote it back unconditionally resurrects the account.
    /// </summary>
    [Fact]
    public async Task Login_stamp_does_not_revert_a_concurrent_deprovision()
    {
        var prefix = $"ls{Guid.NewGuid():N}";
        var (store, _) = NewStore(prefix);

        // Repeat: the interleaving is timing-dependent, so one round could get lucky.
        for (var round = 0; round < 12; round++)
        {
            var id = $"u{round}";
            await store.CreateAsync(User(id));

            using var deprovisioned = new CancellationTokenSource();

            // A burst of logins, continuing across the admin write.
            var logins = Enumerable.Range(0, 24)
                .Select(async i =>
                {
                    if (i % 3 == 0) await Task.Yield();
                    await store.RecordSuccessfulLoginAsync(id);
                })
                .ToArray();

            // Admin deprovisions while those are in flight.
            var admin = Task.Run(async () =>
            {
                await Task.Yield();
                var fresh = await store.GetAsync(id);
                fresh!.IsActive = false;
                fresh.MfaEnabled = true;
                fresh.SecurityStamp = "stamp-v2";
                await store.UpdateAsync(fresh);
                deprovisioned.Cancel();
            });

            await Task.WhenAll(logins.Append(admin));

            var after = await store.GetAsync(id);
            Assert.False(after!.IsActive,
                $"round {round}: a concurrent login stamp resurrected a deprovisioned account");
            Assert.True(after.MfaEnabled,
                $"round {round}: a concurrent login stamp reverted MFA enrolment");
            Assert.Equal("stamp-v2", after.SecurityStamp);
        }
    }

    /// <summary>The stamp must still clear an active lockout — Replace semantics, not a partial merge.</summary>
    [Fact]
    public async Task Login_stamp_still_clears_lockout()
    {
        var prefix = $"lc{Guid.NewGuid():N}";
        var (store, _) = NewStore(prefix);
        var user = User("u2");
        user.LockoutEnabled = true;
        await store.CreateAsync(user);

        // Drive the account into lockout.
        for (var i = 0; i < 5; i++)
            await store.RecordFailedLoginAsync("u2", maxAttempts: 3, lockoutDuration: TimeSpan.FromMinutes(10));

        var locked = await store.GetAsync("u2");
        Assert.NotNull(locked!.LockoutEnd);

        await store.RecordSuccessfulLoginAsync("u2");

        var after = await store.GetAsync("u2");
        Assert.Null(after!.LockoutEnd);
        Assert.Equal(0, after.AccessFailedCount);
    }

    /// <summary>A password rehash written by the stamp must still land.</summary>
    [Fact]
    public async Task Login_stamp_applies_a_password_rehash()
    {
        var prefix = $"lr{Guid.NewGuid():N}";
        var (store, _) = NewStore(prefix);
        await store.CreateAsync(User("u3"));

        await store.RecordSuccessfulLoginAsync("u3", rehashedPassword: "hash-v2");

        var after = await store.GetAsync("u3");
        Assert.Equal("hash-v2", after!.PasswordHash);
    }

    /// <summary>Concurrent logins must not lose the stamp or corrupt the row.</summary>
    [Fact]
    public async Task Concurrent_login_stamps_converge()
    {
        var prefix = $"lp{Guid.NewGuid():N}";
        var (store, _) = NewStore(prefix);
        await store.CreateAsync(User("u4"));

        await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => store.RecordSuccessfulLoginAsync("u4")));

        var after = await store.GetAsync("u4");
        Assert.NotNull(after!.LastLoginAt);
        Assert.Equal(0, after.AccessFailedCount);
        Assert.Equal("stamp-v1", after.SecurityStamp);
    }
}
