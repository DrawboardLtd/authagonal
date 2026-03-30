using System.Text.Json.Serialization;

namespace Authagonal.Server.Endpoints.Scim;

public static class ScimResults
{
    private const string ScimJsonContentType = "application/scim+json";

    public static IResult Success(object value, int statusCode = 200)
        => Results.Json(value, contentType: ScimJsonContentType, statusCode: statusCode);

    public static IResult Created(object value, string? location = null)
    {
        return Results.Json(value, contentType: ScimJsonContentType, statusCode: 201);
    }

    public static IResult Error(int status, string scimType, string detail)
    {
        var error = new ScimError
        {
            Status = status,
            ScimType = scimType,
            Detail = detail,
        };
        return Results.Json(error, contentType: ScimJsonContentType, statusCode: status);
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

    [JsonPropertyName("status")]
    public int Status { get; set; }

    [JsonPropertyName("scimType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ScimType { get; set; }

    [JsonPropertyName("detail")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Detail { get; set; }
}
