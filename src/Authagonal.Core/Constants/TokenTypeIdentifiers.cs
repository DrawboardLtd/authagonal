namespace Authagonal.Core.Constants;

/// <summary>
/// RFC 8693 §3 token type identifiers. Only the two JWT-access-token identifiers are
/// meaningful to this server: it can validate and re-issue its own access tokens, nothing else.
/// </summary>
public static class TokenTypeIdentifiers
{
    public const string AccessToken = "urn:ietf:params:oauth:token-type:access_token";
    public const string Jwt = "urn:ietf:params:oauth:token-type:jwt";
}
