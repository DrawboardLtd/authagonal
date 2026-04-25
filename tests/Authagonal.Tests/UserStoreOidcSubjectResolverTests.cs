using System.Security.Claims;
using Authagonal.Core.Models;
using Authagonal.Protocol;
using Authagonal.Server.Services;
using Authagonal.Tests.Infrastructure;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Authagonal.Tests;

/// <summary>
/// Unit tests for the federation-claim flow-through in <see cref="UserStoreOidcSubjectResolver"/>.
/// `federated:*` cookie claims captured at the OIDC callback must surface on the
/// <see cref="OidcSubject.FederationClaims"/> dictionary so scope-gated emission re-releases them.
/// </summary>
public sealed class UserStoreOidcSubjectResolverTests
{
    private static UserStoreOidcSubjectResolver BuildResolver(AuthUser user)
    {
        var users = new InMemoryUserStore();
        users.CreateAsync(user).GetAwaiter().GetResult();
        return new UserStoreOidcSubjectResolver(users, new InMemoryScimGroupStore(), new InMemoryClientStore());
    }

    private static ClaimsPrincipal Principal(string subjectId, params (string type, string value)[] extra)
    {
        var identity = new ClaimsIdentity(CookieAuthenticationDefaults.AuthenticationScheme);
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, subjectId));
        identity.AddClaim(new Claim("sub", subjectId));
        foreach (var (type, value) in extra)
            identity.AddClaim(new Claim(type, value));
        return new ClaimsPrincipal(identity);
    }

    private static AuthUser ActiveUser(string id = "user-1") => new()
    {
        Id = id,
        Email = "u@example.com",
        NormalizedEmail = "U@EXAMPLE.COM",
        EmailConfirmed = true,
        IsActive = true,
    };

    [Fact]
    public async Task Resolve_PromotesFederatedClaimsToFederationClaims()
    {
        var user = ActiveUser();
        var resolver = BuildResolver(user);

        var principal = Principal(user.Id,
            ("federated:LinkShareToken", "tok-abc"),
            ("federated:OrganizationId", "org-42"),
            ("federated:OrgRole", "Reader"));

        var result = await resolver.ResolveAsync(
            principal,
            new OidcSubjectResolutionContext("client-1", ["openid"], []));

        var subject = Assert.IsType<OidcSubjectResult.Allowed>(result).Subject;
        Assert.NotNull(subject.FederationClaims);
        Assert.Equal("tok-abc", subject.FederationClaims!["LinkShareToken"]);
        Assert.Equal("org-42", subject.FederationClaims["OrganizationId"]);
        Assert.Equal("Reader", subject.FederationClaims["OrgRole"]);
    }

    [Fact]
    public async Task Resolve_NoFederatedClaims_LeavesFederationClaimsNull()
    {
        var user = ActiveUser();
        var resolver = BuildResolver(user);

        var result = await resolver.ResolveAsync(
            Principal(user.Id),
            new OidcSubjectResolutionContext("client-1", ["openid"], []));

        var subject = Assert.IsType<OidcSubjectResult.Allowed>(result).Subject;
        Assert.Null(subject.FederationClaims);
    }

    [Fact]
    public async Task Resolve_FederatedClaims_DoNotPolluteCustomAttributes()
    {
        // CustomAttributes is the per-user record. Federation values are per-session
        // — they live on FederationClaims and never bleed into the user's stored attrs.
        var user = ActiveUser();
        user.CustomAttributes["DeptCode"] = "Eng";
        var resolver = BuildResolver(user);

        var principal = Principal(user.Id,
            ("federated:LinkShareToken", "tok"));

        var result = await resolver.ResolveAsync(
            principal,
            new OidcSubjectResolutionContext("client-1", ["openid"], []));

        var subject = Assert.IsType<OidcSubjectResult.Allowed>(result).Subject;
        Assert.NotNull(subject.CustomAttributes);
        Assert.Equal("Eng", subject.CustomAttributes!["DeptCode"]);
        Assert.False(subject.CustomAttributes.ContainsKey("LinkShareToken"));
    }

    [Fact]
    public async Task ResolveRefresh_PreservesFederationClaimsFromPriorSubject()
    {
        // On refresh the cookie isn't there — federation claims must ride through
        // priorSubject.FederationClaims.
        var user = ActiveUser();
        var resolver = BuildResolver(user);

        var prior = new OidcSubject
        {
            SubjectId = user.Id,
            FederationClaims = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["LinkShareToken"] = "tok-original"
            }
        };

        var result = await resolver.ResolveRefreshAsync(
            prior,
            new OidcSubjectResolutionContext("client-1", ["openid"], []));

        var subject = Assert.IsType<OidcSubjectResult.Allowed>(result).Subject;
        Assert.NotNull(subject.FederationClaims);
        Assert.Equal("tok-original", subject.FederationClaims!["LinkShareToken"]);
    }

    [Fact]
    public async Task Resolve_IgnoresEmptyFederatedClaimName()
    {
        var user = ActiveUser();
        var resolver = BuildResolver(user);

        // `federated:` (no name after the prefix) should not yield a claim.
        var principal = Principal(user.Id, ("federated:", "noise"));
        var result = await resolver.ResolveAsync(
            principal,
            new OidcSubjectResolutionContext("client-1", ["openid"], []));

        var subject = Assert.IsType<OidcSubjectResult.Allowed>(result).Subject;
        Assert.Null(subject.FederationClaims);
    }
}
