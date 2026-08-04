using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using Authagonal.Core.Authority;
using Authagonal.Core.Constants;
using Authagonal.Core.Models;
using Authagonal.Server.Services;
using Authagonal.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Authagonal.Tests;

/// <summary>
/// An agent client on a NON-delegation grant. The narrowest agent used to get the broadest token.
/// </summary>
/// <remarks>
/// <c>AgentMode</c> was enforced in exactly two places — <c>HandleClientCredentialsAsync</c> refuses a
/// <c>Delegated</c>-only profile, <c>HandleTokenExchangeAsync</c> refuses a <c>Service</c>-mode one. No other
/// mint path consulted <c>IAgentProfileStore</c> at all, so <c>authorization_code</c>, <c>refresh_token</c> and
/// <c>device_code</c> minted for an agent exactly as for any other client: no <c>authorization_details</c>, no
/// <c>act</c> chain, no <c>MaxTokenLifetimeSeconds</c> clamp.
/// <para>
/// Absence of the claim is not neutral. <c>AuthorityEvaluator.FromPrincipal</c> returns
/// <c>AuthoritySet.Unrestricted</c> for zero claims — the legacy scope-only compatibility case — and
/// <c>AuthoritySet.Permits</c> short-circuits on <c>IsUnrestricted</c>. So an agent registered with a single
/// ask-gated capability could run a plain authorization_code flow against its own registered redirect URI and
/// receive a token that permits everything, bypassing the ceiling, the standing consent at
/// <c>/consent/agents</c>, the ask-gate, the provenance chain and the depth cap in one step.
/// <c>action_policies</c> has no scope equivalent, so no amount of scope checking at the resource server could
/// have recovered the approval requirement.
/// </para>
/// <para>
/// No test exercised this combination: the agent suites use token exchange or client_credentials only.
/// </para>
/// </remarks>
public sealed class AgentNonDelegationGrantTests : IAsyncLifetime
{
    private const string AgentClientId = "interactive-agent";
    private const string AgentClientSecret = "interactive-agent-secret";
    private const string AllAskAgentClientId = "all-ask-agent";

    private readonly AuthagonalTestFactory _factory = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await _factory.SeedTestDataAsync();
        await _factory.SeedTestUserAsync();

        var hasher = _factory.Services.GetRequiredService<PasswordHasher>();

        // The registration the finding describes: an AI-assistant product whose backend delegates and whose
        // web UI signs the user in, so it holds the interactive grants as well.
        foreach (var id in new[] { AgentClientId, AllAskAgentClientId })
        {
            await _factory.ClientStore.UpsertAsync(new OAuthClient
            {
                ClientId = id,
                ClientName = id,
                RequireClientSecret = false,
                ClientSecretHashes = [hasher.HashPassword(AgentClientSecret)],
                AllowedGrantTypes =
                [
                    GrantTypes.TokenExchange, GrantTypes.AuthorizationCode, GrantTypes.RefreshToken,
                ],
                AllowedScopes = ["openid", "profile", "email", "offline_access"],
                RedirectUris = ["https://agent.test/callback"],
                AccessTokenLifetimeSeconds = 3600,
                AllowOfflineAccess = true,
            });
        }

        // Some authority available without asking.
        await _factory.AgentProfileStore.UpsertAsync(new AgentProfile
        {
            ClientId = AgentClientId,
            Mode = AgentMode.Both,
            Ceiling = AuthoritySet.Of(
                new AuthorityGrant { Type = "calendar", Actions = ["read"] },
                new AuthorityGrant
                {
                    Type = "payments",
                    Actions = ["transfer"],
                    ActionPolicies = new Dictionary<string, ActionPolicy>
                    {
                        ["transfer"] = ActionPolicy.Ask,
                    },
                }),
            MaxDelegationDepth = 0,
            MaxTokenLifetimeSeconds = 300,
        });

        // Nothing available without asking — the most tightly configured agent there is.
        await _factory.AgentProfileStore.UpsertAsync(new AgentProfile
        {
            ClientId = AllAskAgentClientId,
            Mode = AgentMode.Both,
            Ceiling = AuthoritySet.Of(new AuthorityGrant
            {
                Type = "payments",
                Actions = ["transfer"],
                ActionPolicies = new Dictionary<string, ActionPolicy> { ["transfer"] = ActionPolicy.Ask },
            }),
            MaxDelegationDepth = 0,
            MaxTokenLifetimeSeconds = 300,
        });
    }

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    /// <summary>
    /// An authorization_code token for an agent carries the ceiling, not nothing.
    /// </summary>
    [Fact]
    public async Task AuthorizationCode_ForAnAgent_MintsTheCeilingRatherThanUnrestrictedAuthority()
    {
        var tokens = await AuthorizationCodeAsync(AgentClientId);
        var access = tokens.GetProperty("access_token").GetString()!;

        var authority = AuthorityOf(access);

        // The claim is present at all — which is the whole finding: absent reads as unrestricted.
        Assert.NotNull(authority);

        // And it is the ceiling with ask degraded to deny, because this grant has nobody to ask.
        Assert.Contains("calendar", authority!, StringComparison.Ordinal);
        Assert.DoesNotContain("payments", authority!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The refresh of that token stays bounded too — rotation must not launder the ceiling away.
    /// </summary>
    [Fact]
    public async Task RefreshToken_ForAnAgent_KeepsTheCeiling()
    {
        var tokens = await AuthorizationCodeAsync(AgentClientId);
        var refresh = tokens.GetProperty("refresh_token").GetString()!;

        var refreshed = await _client.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refresh,
                ["client_id"] = AgentClientId,
            }));
        refreshed.EnsureSuccessStatusCode();

        var body = await refreshed.Content.ReadFromJsonAsync<JsonElement>();
        var authority = AuthorityOf(body.GetProperty("access_token").GetString()!);

        Assert.NotNull(authority);
        Assert.Contains("calendar", authority!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The agent's lifetime cap clamps a grant that never consulted the profile before.
    /// </summary>
    [Fact]
    public async Task AuthorizationCode_ForAnAgent_IsClampedToTheProfileLifetime()
    {
        var tokens = await AuthorizationCodeAsync(AgentClientId);
        var access = tokens.GetProperty("access_token").GetString()!;

        var payload = PayloadOf(access);
        var exp = payload.GetProperty("exp").GetInt64();
        var iat = payload.GetProperty("iat").GetInt64();

        // MaxTokenLifetimeSeconds is 300; the client's own AccessTokenLifetimeSeconds is 3600.
        Assert.True(exp - iat <= 300, $"token lived {exp - iat}s, expected <= 300s");
    }

    /// <summary>
    /// An agent whose ceiling grants nothing unattended is refused rather than given a blank cheque.
    /// </summary>
    /// <remarks>
    /// The same rule <c>HandleClientCredentialsAsync</c> already applied: ask degrades to deny where there is
    /// no one to ask, and a ceiling that then permits nothing has no safe token to mint — omitting the claim
    /// would read as unrestricted, which is the defect being fixed. Refusing names the remedy.
    /// </remarks>
    [Fact]
    public async Task AuthorizationCode_ForAnAllAskAgent_IsRefusedRatherThanUnbounded()
    {
        var (code, verifier) = await AuthorizeForCodeAsync(AllAskAgentClientId);

        var response = await _client.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = "https://agent.test/callback",
                ["code_verifier"] = verifier,
                ["client_id"] = AllAskAgentClientId,
            }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("unauthorized_client", body, StringComparison.Ordinal);
    }

    /// <summary>The control: a NON-agent client is untouched by any of this.</summary>
    [Fact]
    public async Task AuthorizationCode_ForAnOrdinaryClient_StillCarriesNoAuthorityClaim()
    {
        var tokens = await AuthorizationCodeAsync(AuthagonalTestFactory.TestClientId, "https://app.test/callback");

        // Zero claims stays the legacy scope-only case for clients that are not agents.
        Assert.Null(AuthorityOf(tokens.GetProperty("access_token").GetString()!));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<JsonElement> AuthorizationCodeAsync(
        string clientId, string redirectUri = "https://agent.test/callback")
    {
        var (code, verifier) = await AuthorizeForCodeAsync(clientId, redirectUri);

        var response = await _client.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["code_verifier"] = verifier,
                ["client_id"] = clientId,
            }));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<(string Code, string Verifier)> AuthorizeForCodeAsync(
        string clientId, string redirectUri = "https://agent.test/callback")
    {
        await _client.PostAsJsonAsync("/api/auth/login",
            new { email = "test@example.com", password = "Test1234!" });

        var (verifier, challenge) = GeneratePkce();

        var url = $"/connect/authorize?client_id={clientId}" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            "&response_type=code&scope=openid+profile+email+offline_access" +
            $"&state=test&code_challenge={challenge}&code_challenge_method=S256";

        var authorize = await _client.GetAsync(url);
        var code = HttpUtility.ParseQueryString(authorize.Headers.Location!.Query)["code"]!;
        return (code, verifier);
    }

    /// <summary>The raw authorization_details claim, or null when the token carries none.</summary>
    private static string? AuthorityOf(string jwt)
    {
        var payload = PayloadOf(jwt);
        return payload.TryGetProperty(AuthorityClaims.AuthorizationDetails, out var value)
            ? value.GetRawText()
            : null;
    }

    private static JsonElement PayloadOf(string jwt)
    {
        var part = jwt.Split('.')[1];
        var padded = part.Replace('-', '+').Replace('_', '/')
            .PadRight(part.Length + (4 - part.Length % 4) % 4, '=');
        return JsonDocument.Parse(Convert.FromBase64String(padded)).RootElement.Clone();
    }

    private static (string Verifier, string Challenge) GeneratePkce()
    {
        var verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return (verifier, challenge);
    }
}
