using System.Text;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.AzureProvider.Entities;
using Authagonal.AzureProvider.Stores;
using Authagonal.Tests.Infrastructure;
using Azure.Data.Tables;

namespace Authagonal.Tests;

/// <summary>
/// <c>EnumerateLoginStatesAsync</c> (F27): the whole-population retention sweep must stream every
/// user's non-PII login-state columns without invoking the field cipher — the old path decrypted
/// every profile of every tenant hourly. Verified against Azurite with a cipher that counts (and
/// would fail) any decrypt call.
/// </summary>
[Collection("Azurite")]
public class LoginStateEnumerationTests(AzuriteFixture azurite)
{
    /// <summary>Protects normally but THROWS on Resolve — proving enumeration never decrypts.</summary>
    private sealed class TripwireCipher : IFieldCipher
    {
        public Task<string> ProtectAsync(string plaintext, CancellationToken ct = default)
            => Task.FromResult("enc:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext)));
        public Task<string> ResolveAsync(string stored, CancellationToken ct = default)
            => throw new InvalidOperationException("login-state enumeration must not decrypt");
    }

    private readonly TableServiceClient _svc = new(azurite.ConnectionString);

    private TableUserStore NewStore(string prefix, IFieldCipher? cipher, EnvPartitioner? part = null)
    {
        TableClient T(string name)
        {
            var c = _svc.GetTableClient($"{prefix}{name}");
            c.CreateIfNotExists();
            return c;
        }
        return new TableUserStore(T("Users"), T("Emails"), T("Logins"), T("ExtIds"), null, null,
            part ?? EnvPartitioner.Live, fieldCipher: cipher);
    }

    [Fact]
    public async Task Streams_login_state_for_every_user_without_decrypting()
    {
        var prefix = "LoginState" + Guid.NewGuid().ToString("N")[..8];
        var writeStore = NewStore(prefix, new PassthroughProtectCipher());

        var lastLogin = DateTimeOffset.UtcNow.AddDays(-40);
        await writeStore.CreateAsync(new AuthUser
        {
            Id = "u1", Email = "one@example.com", NormalizedEmail = "ONE@EXAMPLE.COM",
            Phone = "+15550001111", IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-100), LastLoginAt = lastLogin,
        });
        await writeStore.CreateAsync(new AuthUser
        {
            Id = "u2", Email = "two@example.com", NormalizedEmail = "TWO@EXAMPLE.COM",
            IsActive = false, CreatedAt = DateTimeOffset.UtcNow.AddDays(-10),
        });

        // Read with the tripwire: any decrypt during enumeration throws.
        var readStore = NewStore(prefix, new TripwireCipher());
        var states = new Dictionary<string, UserLoginState>();
        await foreach (var s in readStore.EnumerateLoginStatesAsync())
            states[s.Id] = s;

        Assert.Equal(2, states.Count);
        Assert.True(states["u1"].IsActive);
        Assert.Equal(lastLogin, states["u1"].LastLoginAt!.Value, TimeSpan.FromSeconds(1));
        Assert.False(states["u2"].IsActive);
        Assert.Null(states["u2"].LastLoginAt);
    }

    [Fact]
    public async Task Sandbox_env_enumeration_stays_inside_its_partition_range()
    {
        var prefix = "LoginStateEnv" + Guid.NewGuid().ToString("N")[..8];
        var live = NewStore(prefix, null);
        var sandbox = NewStore(prefix, null, new EnvPartitioner("test1"));

        await live.CreateAsync(new AuthUser { Id = "live-user", Email = "l@example.com", NormalizedEmail = "L@EXAMPLE.COM", CreatedAt = DateTimeOffset.UtcNow });
        await sandbox.CreateAsync(new AuthUser { Id = "sbx-user", Email = "s@example.com", NormalizedEmail = "S@EXAMPLE.COM", CreatedAt = DateTimeOffset.UtcNow });

        var sandboxIds = new List<string>();
        await foreach (var s in sandbox.EnumerateLoginStatesAsync())
            sandboxIds.Add(s.Id);

        Assert.Equal(["sbx-user"], sandboxIds);
    }

    private sealed class PassthroughProtectCipher : IFieldCipher
    {
        public Task<string> ProtectAsync(string plaintext, CancellationToken ct = default)
            => Task.FromResult("enc:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext)));
        public Task<string> ResolveAsync(string stored, CancellationToken ct = default)
            => Task.FromResult(stored.StartsWith("enc:", StringComparison.Ordinal)
                ? Encoding.UTF8.GetString(Convert.FromBase64String(stored[4..]))
                : stored);
    }
}
