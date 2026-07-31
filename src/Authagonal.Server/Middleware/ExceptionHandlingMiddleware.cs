using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Localization;

namespace Authagonal.Server.Middleware;

public sealed class ExceptionHandlingMiddleware(
    RequestDelegate next,
    ILogger<ExceptionHandlingMiddleware> logger,
    IStringLocalizer<SharedMessages> localizer)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            var correlationId = Activity.Current?.Id ?? context.TraceIdentifier;

            // Nothing can be written once the response has started — the status line and headers are
            // already on the wire, so setting StatusCode throws and the original exception is lost
            // behind a second one. Let the server abort the connection instead, which is what a
            // truncated response should look like to the client.
            if (context.Response.HasStarted)
            {
                logger.LogError(
                    ex,
                    "Unhandled exception AFTER the response started; connection will be aborted. " +
                    "CorrelationId: {CorrelationId}, Path: {Path}, Method: {Method}",
                    correlationId, context.Request.Path, context.Request.Method);
                throw;
            }

            logger.LogError(
                ex,
                "Unhandled exception occurred. CorrelationId: {CorrelationId}, Path: {Path}, Method: {Method}",
                correlationId,
                context.Request.Path,
                context.Request.Method);

            // An UNHANDLED exception is a server fault, and answering 4xx says the opposite: it tells
            // the caller their request was wrong and tells operators nothing is broken. Both
            // ArgumentException and InvalidOperationException are overwhelmingly internal invariant
            // failures — an endpoint that means "bad request" returns one deliberately rather than
            // throwing — so mapping them to 400 turned real faults into silent client errors that no
            // 5xx alert ever fired on.
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;

            context.Response.ContentType = "application/json";

            var errorDescription = localizer["Error_ServerError"].Value;

            var errorResponse = new ErrorResponse
            {
                Error = "server_error",
                ErrorDescription = errorDescription,
                CorrelationId = correlationId
            };

            await context.Response.WriteAsync(
                JsonSerializer.Serialize(errorResponse, AuthagonalJsonContext.Default.ErrorResponse));
        }
    }
}

public static class ExceptionHandlingMiddlewareExtensions
{
    public static IApplicationBuilder UseExceptionHandlingMiddleware(this IApplicationBuilder app)
    {
        return app.UseMiddleware<ExceptionHandlingMiddleware>();
    }
}

internal sealed class ErrorResponse
{
    [JsonPropertyName("error")]
    public required string Error { get; set; }
    [JsonPropertyName("error_description")]
    public required string ErrorDescription { get; set; }
    [JsonPropertyName("correlation_id")]
    public required string CorrelationId { get; set; }
}
