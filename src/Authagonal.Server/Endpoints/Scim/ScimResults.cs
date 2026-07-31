using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace Authagonal.Server.Endpoints.Scim;

public static class ScimResults
{
    private const string ScimJsonContentType = "application/scim+json";

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "SCIM resources are polymorphic")]
    public static IResult Success(object value, int statusCode = 200)
        => Results.Json(value, contentType: ScimJsonContentType, statusCode: statusCode);

    /// <summary>
    /// Serializes with property names exactly as declared, for payloads whose member casing is fixed
    /// by the specification.
    /// </summary>
    /// <remarks>
    /// RFC 7644 §3.4.2 names the ListResponse member <c>Resources</c>, capital R — it is one of the
    /// few members in SCIM that is not lowerCamelCase. The default minimal-API naming policy
    /// camelCased it to <c>resources</c>, so a conforming client parsing /Schemas or /ResourceTypes
    /// found no resource list at all.
    /// </remarks>
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "SCIM discovery payloads are polymorphic")]
    public static IResult SuccessVerbatim(object value, int statusCode = 200)
        => Results.Json(
            value,
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = null },
            contentType: ScimJsonContentType,
            statusCode: statusCode);

    /// <summary>
    /// A 201 carrying the <c>Location</c> header RFC 7644 §3.3 requires.
    /// </summary>
    /// <remarks>
    /// The parameter was accepted and then discarded, so every SCIM create answered without a
    /// Location. A provisioning client that follows the header — which is the documented way to
    /// address the new resource — had nothing to follow.
    /// </remarks>
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "SCIM resources are polymorphic")]
    public static IResult Created(object value, string? location = null)
    {
        var inner = Results.Json(value, contentType: ScimJsonContentType, statusCode: 201);
        return string.IsNullOrEmpty(location) ? inner : new WithLocation(inner, location);
    }

    private sealed class WithLocation(IResult inner, string location) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.Location = location;
            return inner.ExecuteAsync(httpContext);
        }
    }

    public static IResult Error(int status, string scimType, string detail)
    {
        var error = new ScimError
        {
            Status = status.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ScimType = scimType,
            Detail = detail,
        };
        return TypedResults.Json(error, AuthagonalJsonContext.Default.ScimError, contentType: ScimJsonContentType, statusCode: status);
    }

    public static IResult NotFound(string detail)
        => Error(404, "invalidValue", detail);

    public static IResult BadRequest(string detail)
        => Error(400, "invalidValue", detail);

    public static IResult Conflict(string detail)
        => Error(409, "uniqueness", detail);

    public static IResult NoContent()
        => Results.NoContent();
}

public sealed class ScimError
{
    [JsonPropertyName("schemas")]
    public string[] Schemas { get; set; } = ["urn:ietf:params:scim:api:messages:2.0:Error"];

    /// <summary>
    /// RFC 7644 §3.12 types this member as a string ("The HTTP status code … expressed as a JSON
    /// string"). It was emitted as a JSON number, so a strict client deserializing the schema's own
    /// type failed on every error response — the one response it most needs to read.
    /// </summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("scimType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ScimType { get; set; }

    [JsonPropertyName("detail")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Detail { get; set; }
}
