using Microsoft.AspNetCore.Http;

namespace Authagonal.Protocol.Endpoints;

internal static class JsonResults
{
    public static IResult OAuthError(string error, string description, int statusCode = 400)
        => TypedResults.Json(
            new OAuthErrorResponse { Error = error, ErrorDescription = description },
            ProtocolJsonContext.Default.OAuthErrorResponse,
            statusCode: statusCode);
}
