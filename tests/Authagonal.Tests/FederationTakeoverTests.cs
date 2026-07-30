using System.Net;
using System.Net.Http.Json;
using Authagonal.Core.Models;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

/// <summary>
/// The chain that turned "provision an account" into "authenticate as a corporate user at every relying
/// party": SCIM could mint a PRE-VERIFIED account for any email address, and
/// <c>/api/auth/forgot-password</c> would then issue a reset for it — <c>ResetPasswordAsync</c> sets
/// <c>PasswordHash</c> unconditionally, with no SSO-only or has-a-password precondition. So after
/// repointing a bound account's email to an attacker-controlled address, the attacker completed a password
/// reset and signed in as the real user's <c>sub</c>, bypassing the upstream IdP entirely. Only local MFA
/// stood in the way, and that is typically absent in tenants that enforce MFA upstream.
/// </summary>
public sealed class FederationTakeoverTests : IAsyncDisposable
{
    private readonly AuthagonalTestFactory _factory = new();

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    /// <summary>
    /// An SSO-only account has no password to reset, so no reset link may be issued for it. Response stays
    /// enumeration-neutral (the endpoint always reports success), so the assertion is on the effect.
    /// </summary>
    [Fact]
    public async Task Forgot_password_issues_no_reset_for_an_sso_only_account()
    {
        await _factory.SeedTestDataAsync();

        // A federated/SCIM-shaped account: pre-verified email, no local password.
        var ssoUser = new AuthUser
        {
            Id = Guid.NewGuid().ToString("N"),
            Email = "sso-only@acme.com",
            NormalizedEmail = "SSO-ONLY@ACME.COM",
            EmailConfirmed = true,
            PasswordHash = null,
            IsActive = true,
            LockoutEnabled = true,
            SecurityStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await _factory.UserStore.CreateAsync(ssoUser);

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await client.PostAsJsonAsync("/api/auth/forgot-password",
            new { email = "sso-only@acme.com" });

        // Enumeration-neutral success...
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // ...but NO reset token was staged. The token is persisted as a `password_reset` grant, so its
        // absence is what proves no usable reset link was minted — checking PasswordHash would prove
        // nothing here, because the hash is only written when the token is later redeemed.
        var grants = await _factory.GrantStore.GetBySubjectAsync(ssoUser.Id);
        Assert.DoesNotContain(grants, g => g.Type == "password_reset");
    }

    /// <summary>A normal account with a password must still be able to reset it.</summary>
    [Fact]
    public async Task Forgot_password_still_works_for_a_password_account()
    {
        await _factory.SeedTestDataAsync();
        var user = await _factory.SeedTestUserAsync();
        Assert.False(string.IsNullOrEmpty(user.PasswordHash));

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await client.PostAsJsonAsync("/api/auth/forgot-password",
            new { email = user.Email });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // A reset token WAS staged for a real password account.
        var grants = await _factory.GrantStore.GetBySubjectAsync(user.Id);
        Assert.Contains(grants, g => g.Type == "password_reset");
    }

    /// <summary>
    /// A SCIM userName that is not an address must be refused: it would otherwise be stored as a
    /// pre-verified email and become a storage key, an index entry, and an input to account linking.
    /// </summary>
    [Theory]
    [InlineData("not-an-email")]
    [InlineData("no-at-sign.example.com")]
    [InlineData("two@@at.example.com")]
    [InlineData("trailing@")]
    [InlineData("@leading.example.com")]
    [InlineData("no-dot@localdomain")]
    [InlineData("has space@acme.com")]
    public async Task Scim_refuses_a_userName_that_is_not_an_email(string userName)
    {
        await _factory.SeedTestDataAsync();
        var client = await ScimClientAsync();

        var response = await client.PostAsJsonAsync("/scim/v2/Users", new
        {
            schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:User" },
            userName,
            active = true,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>A well-formed address is still accepted — the guard must not break provisioning.</summary>
    [Fact]
    public async Task Scim_still_provisions_a_valid_address()
    {
        await _factory.SeedTestDataAsync();
        var client = await ScimClientAsync();

        var response = await client.PostAsJsonAsync("/scim/v2/Users", new
        {
            schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:User" },
            userName = "valid.user@acme.com",
            active = true,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private async Task<HttpClient> ScimClientAsync()
    {
        var (_, rawToken) = await _factory.SeedScimClientAsync();
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", rawToken);
        return client;
    }
}
