using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.WebUtilities;

namespace SwiftRide.TripService.Api;

public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var statusCode = exception is BadHttpRequestException { StatusCode: >= 400 and < 500 } badRequest
            ? badRequest.StatusCode
            : StatusCodes.Status500InternalServerError;
        var traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

        // Exception messages can contain payment tokens or connection strings.
        logger.Log(statusCode >= 500 ? LogLevel.Error : LogLevel.Warning,
            "Request failed with {StatusCode}. Exception type: {ExceptionType}. TraceId: {TraceId}",
            statusCode, exception.GetType().FullName, traceId);

        await Results.Problem(
            statusCode: statusCode,
            title: ReasonPhrases.GetReasonPhrase(statusCode),
            detail: statusCode >= 500
                ? "An unexpected error occurred. Contact support with the traceId."
                : "The request could not be processed. Check the request format and size.",
            extensions: new Dictionary<string, object?> { ["traceId"] = traceId })
            .ExecuteAsync(httpContext);
        return true;
    }
}

