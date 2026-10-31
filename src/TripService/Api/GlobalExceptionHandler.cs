using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.WebUtilities;
using SwiftRide.TripService.Application.Exceptions;
using SwiftRide.TripService.Domain;

namespace SwiftRide.TripService.Api;

public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var statusCode = exception switch
        {
            TripNotFoundException => StatusCodes.Status404NotFound,
            UnauthorizedTripAccessException => StatusCodes.Status403Forbidden,
            IdempotencyConflictException => StatusCodes.Status409Conflict,
            ActiveTripConflictException => StatusCodes.Status409Conflict,
            InvalidTripCommandException => StatusCodes.Status400BadRequest,
            TripDomainException domainEx when domainEx.Code is "InvalidTransition" or "RematchPending" => StatusCodes.Status409Conflict,
            TripDomainException => StatusCodes.Status400BadRequest,
            BadHttpRequestException badReq => badReq.StatusCode,
            _ => StatusCodes.Status500InternalServerError
        };

        var traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

        logger.Log(statusCode >= 500 ? LogLevel.Error : LogLevel.Warning, exception,
            "Trip API request failed with {StatusCode}. Exception type: {ExceptionType}. TraceId: {TraceId}",
            statusCode, exception.GetType().FullName, traceId);

        var title = ReasonPhrases.GetReasonPhrase(statusCode);
        var detail = statusCode switch
        {
            StatusCodes.Status404NotFound => "The requested trip was not found.",
            StatusCodes.Status403Forbidden => "Access to this trip resource or action is forbidden.",
            StatusCodes.Status409Conflict => "The request conflicts with the current trip state or idempotency key.",
            StatusCodes.Status400BadRequest => "The request is invalid or violates domain rules.",
            _ => "An unexpected error occurred. Contact support with the traceId."
        };

        await Results.Problem(
            statusCode: statusCode,
            title: title,
            detail: detail,
            extensions: new Dictionary<string, object?> { ["traceId"] = traceId })
            .ExecuteAsync(httpContext);

        return true;
    }
}
