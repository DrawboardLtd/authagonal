using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Localization;

namespace Authagonal.Server.Middleware;

public sealed class ExceptionHandlingMiddleware(
    RequestDelegate next,
    ILogger<ExceptionHandlingMiddleware> logger,
    IStringLocalizer<SharedMessages> localizer)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            var correlationId = Activity.Current?.Id ?? context.TraceIdentifier;

            logger.LogError(
                ex,
                "Unhandled exception occurred. CorrelationId: {CorrelationId}, Path: {Path}, Method: {Method}",
                correlationId,
                context.Request.Path,
                context.Request.Method);

            context.Response.StatusCode = ex switch
            {
                ArgumentException => StatusCodes.Status400BadRequest,
                UnauthorizedAccessException => StatusCodes.Status401Unauthorized,
                KeyNotFoundException => StatusCodes.Status404NotFound,
                InvalidOperationException => StatusCodes.Status400BadRequest,
                _ => StatusCodes.Status500InternalServerError
            };

            context.Response.ContentType = "application/json";

            var errorDescription = context.Response.StatusCode switch
            {
                StatusCodes.Status400BadRequest => localizer["Error_BadRequest"].Value,
                StatusCodes.Status401Unauthorized => localizer["Error_Unauthorized"].Value,
                StatusCodes.Status404NotFound => localizer["Error_NotFound"].Value,
                _ => localizer["Error_ServerError"].Value
            };

            var errorResponse = new
            {
                error = "server_error",
                error_description = errorDescription,
                correlation_id = correlationId
            };

            await context.Response.WriteAsync(
                JsonSerializer.Serialize(errorResponse, JsonOptions));
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
