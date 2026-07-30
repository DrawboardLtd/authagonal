using System.Net.Http.Json;
using System.Text.Json;
using Authagonal.Core.Models;
using Authagonal.Server.Services;
using Authagonal.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Authagonal.Tests;

/// <summary>
/// Passwordless passkey sign-in marks the session <c>mfa_authenticated</c> and the docs describe it as
/// strong authentication, but user verification was <c>Preferred</c> on every path and the resulting UV
/// flag was never inspected. A passkey asserted without UV proves only possession of an unlocked device, so
/// a stolen unlocked phone satisfied an MFA-required policy.
///
/// Requiring UV on the passwordless assertion is load-bearing rather than advisory: Fido2 enforces the
/// requirement itself inside MakeAssertionAsync, and a conforming browser will not produce an assertion
/// without it. Driven through the endpoints because <c>WebAuthnService</c> derives the relying-party id
/// from the active request and cannot be called outside one.
/// </summary>
public sealed class WebAuthnUserVerificationTests : IAsyncDisposable
{
    private readonly AuthagonalTestFactory _factory = new();

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private static string UserVerificationOf(string json)
    {
        // The options may be returned at the root or nested under "options", depending on the endpoint.
        var root = JsonDocument.Parse(json).RootElement;
        if (root.TryGetProperty("options", out var nested)) root = nested;
        return root.GetProperty("userVerification").GetString()!;
    }

    /// <summary>
    /// Passwordless: the passkey is the only factor, so UV must be Required.
    /// </summary>
    [Fact]
    public async Task Passwordless_begin_requires_user_verification()
    {
        await _factory.SeedTestDataAsync();
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var begin = await client.PostAsync("/api/auth/mfa/passwordless/begin", null);
        begin.EnsureSuccessStatusCode();

        Assert.Equal("required", UserVerificationOf(await begin.Content.ReadAsStringAsync()),
            ignoreCase: true);
    }

    /// <summary>
    /// Second factor: a password has already been presented, so possession alone is a genuine second
    /// factor and Preferred keeps older authenticators working. Guards against over-tightening the fix into
    /// a lockout for users whose security key has no PIN.
    /// </summary>
    [Fact]
    public async Task Second_factor_challenge_keeps_user_verification_preferred()
    {
        await _factory.SeedTestDataAsync();
        var user = await _factory.SeedTestUserAsync();
        user.MfaEnabled = true;
        await _factory.UserStore.UpdateAsync(user);

        // A confirmed passkey, so login's challenge carries WebAuthn assertion options.
        await _factory.MfaStore.CreateCredentialAsync(new MfaCredential
        {
            Id = Guid.NewGuid().ToString("N"),
            UserId = user.Id,
            Type = MfaCredentialType.WebAuthn,
            Name = "Passkey",
            PublicKeyJson =
                """{"credentialId":"AAECAwQFBgcICQoLDA0ODw==","publicKey":"AAECAwQFBgcICQoLDA0ODw==","credType":"public-key","aaguid":"00000000-0000-0000-0000-000000000000"}""",
            CreatedAt = DateTimeOffset.UtcNow,
        });

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { email = "test@example.com", password = "Test1234!" });
        login.EnsureSuccessStatusCode();

        var body = await login.Content.ReadFromJsonAsync<JsonElement>();
        // Only assert when the challenge actually carried passkey options.
        if (!body.TryGetProperty("webAuthnOptions", out var opts) || opts.ValueKind == JsonValueKind.Null)
            return;

        Assert.Equal("preferred", opts.GetProperty("userVerification").GetString(), ignoreCase: true);
    }
}
