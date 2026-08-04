using System.Security.Claims;

namespace Authagonal.Server.Endpoints.Scim;

/// <summary>
/// Who a SCIM write is attributed to in the audit trail.
/// </summary>
/// <remarks>
/// The sibling of <c>AdminActor</c>, and separate from it because the actor here is not a person: a SCIM
/// caller authenticates as a provisioning CLIENT holding a static bearer token, so <c>client_id</c> and
/// <c>token_id</c> are the identifying facts and there is no email or subject to prefer over them. The token
/// id matters because a client may hold several and revoking the right one requires knowing which acted.
/// <para>
/// The whole SCIM write surface previously wrote no audit row at all, so "who deactivated this user, and when"
/// had nowhere to look — while the same trail faithfully recorded an administrator renaming a client.
/// </para>
/// </remarks>
internal static class ScimActor
{
    public static string Of(HttpContext http)
    {
        var clientId = http.User.FindFirstValue("client_id");
        var tokenId = http.User.FindFirstValue("token_id");

        return (clientId, tokenId) switch
        {
            (null or "", null or "") => "unknown",
            (_, null or "") => $"scim:{clientId}",
            (null or "", _) => $"scim:token/{tokenId}",
            _ => $"scim:{clientId}/token/{tokenId}",
        };
    }
}
