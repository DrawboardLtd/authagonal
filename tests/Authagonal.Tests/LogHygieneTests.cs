using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Authagonal.Core.Services;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

/// <summary>
/// What must never reach an application log: forged records, bearer-token material, login identifiers.
/// </summary>
public sealed class LogHygieneTests : IAsyncDisposable
{
    private readonly AuthagonalTestFactory _factory = new();

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    // -----------------------------------------------------------------------
    // #245 — attacker-supplied text from an anonymous endpoint
    // -----------------------------------------------------------------------

    /// <summary>
    /// A line-oriented sink treats CR/LF as a record boundary, so caller-supplied text containing them
    /// writes a second entry that reads as genuine — the attacker authors the log an incident is later
    /// reconstructed from.
    /// </summary>
    [Fact]
    public void LogSafe_NeutralisesRecordSeparators()
    {
        var forged = LogSafe.Text("evil.test\r\n2026-08-01 INFO Admin sign-in from 10.0.0.1");

        Assert.DoesNotContain('\r', forged);
        Assert.DoesNotContain('\n', forged);
        Assert.Contains("evil.test", forged);
    }

    /// <summary>Unbounded length is the same attack by volume.</summary>
    [Fact]
    public void LogSafe_CapsLength()
    {
        Assert.True(LogSafe.Text(new string('a', 10_000)).Length <= 65);
    }

    /// <summary>The domain is what the line is diagnostic for; the login identifier is not.</summary>
    [Fact]
    public void LogSafe_MasksTheLocalPart()
    {
        Assert.Equal("a***@example.com", LogSafe.Email("alice@example.com"));
        Assert.Equal("(none)", LogSafe.Email(null));
    }

    /// <summary>
    /// The end-to-end path: POST /api/auth/forgot-password is anonymous and validates nothing before the
    /// address reaches the log, and the domain it logs is everything after the last '@' — still fully
    /// caller-controlled.
    /// </summary>
    [Fact]
    public async Task ForgotPassword_CannotInjectALogRecord()
    {
        await _factory.SeedTestDataAsync();
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/forgot-password", new
        {
            email = "nobody@evil.test\r\n2026-08-01 INFO Admin sign-in from 10.0.0.1",
        });
        response.EnsureSuccessStatusCode();

        // The payload survives as text — it is the record SEPARATORS that make it a forged entry, and they
        // are gone, so the whole thing stays inside the one line it belongs to.
        var line = Assert.Single(_factory.LogSink.Messages.Where(m => m.Contains("evil.test", StringComparison.Ordinal)));
        Assert.DoesNotContain('\r', line);
        Assert.DoesNotContain('\n', line);
    }

    /// <summary>
    /// Registration is anonymous too, and its log lines carry the address itself. They now carry the
    /// masked form: application logs travel much further than the user store.
    /// </summary>
    [Fact]
    public async Task Registration_DoesNotRecordTheFullAddress()
    {
        await _factory.SeedTestDataAsync();
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/register", new
        {
            email = "loguser@example.com",
            password = "Test1234!",
            firstName = "Log",
            lastName = "User",
        });
        response.EnsureSuccessStatusCode();

        Assert.DoesNotContain(_factory.LogSink.Messages,
            m => m.Contains("loguser@example.com", StringComparison.OrdinalIgnoreCase));
        // Non-vacuity: the line is still there, still diagnostic.
        Assert.Contains(_factory.LogSink.Messages,
            m => m.Contains("l***@example.com", StringComparison.Ordinal));
    }

    // -----------------------------------------------------------------------
    // #345 / #245 — SCIM bearer token material
    // -----------------------------------------------------------------------

    /// <summary>
    /// Every SCIM request wrote the presented token's exact length and the first 12 hex of its SHA-256 at
    /// Information, successes included. That is a per-token fingerprint for anyone with log read access —
    /// enough to correlate a stolen token with its traffic — plus a length oracle over failed attempts.
    /// </summary>
    [Fact]
    public async Task ScimRequests_LogNothingDerivedFromTheToken()
    {
        await _factory.SeedTestDataAsync();
        var (_, rawToken) = await _factory.SeedScimClientAsync();
        var client = _factory.CreateClient();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rawToken);
        (await client.GetAsync("/scim/v2/Users")).EnsureSuccessStatusCode();

        const string bogus = "not-a-real-scim-token";
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bogus);
        await client.GetAsync("/scim/v2/Users");

        foreach (var presented in new[] { rawToken, bogus })
        {
            var hashPrefix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(presented)))
                .ToLowerInvariant()[..12];

            Assert.DoesNotContain(_factory.LogSink.Messages, m => m.Contains(presented, StringComparison.Ordinal));
            Assert.DoesNotContain(_factory.LogSink.Messages, m => m.Contains(hashPrefix, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(_factory.LogSink.Messages,
                m => m.Contains($"length={presented.Length}", StringComparison.Ordinal));
        }
    }
}
