using System.Security.Claims;

namespace Authagonal.Server.Endpoints.Admin;

/// <summary>
/// Who an admin write is attributed to in the audit trail.
/// </summary>
/// <remarks>
/// ClientEndpoints, ProvisioningEndpoints and AgentEndpoints each carry a private copy of this; the
/// endpoints that had no audit call at all needed one, and a fourth and fifth copy would only drift.
/// Attribution is the whole point of the row: an admin credential is a bearer token, so "a SAML
/// connection was created for acme.com" is not the fact an incident responder needs — "and this
/// subject created it" is.
/// </remarks>
internal static class AdminActor
{
    public static string Of(HttpContext http) =>
        http.User.FindFirstValue(ClaimTypes.Email)
        ?? http.User.FindFirstValue("email")
        ?? http.User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? http.User.FindFirstValue("sub")
        ?? http.User.FindFirstValue("client_id")
        ?? "unknown";
}
