using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.WebUtilities;
using SwiftRide.PaymentService.Application.Exceptions;
using SwiftRide.PaymentService.Domain.Exceptions;

namespace SwiftRide.PaymentService.Api;

public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (statusCode, detail) = Classify(exception);                  // 34.1 Thay cho dòng tính statusCode cũ
        var traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

        logger.Log(statusCode >= 500 ? LogLevel.Error : LogLevel.Warning,
            "Request failed with {StatusCode}. Exception type: {ExceptionType}. TraceId: {TraceId}",
            statusCode, exception.GetType().FullName, traceId);          // 34.2 Giữ nguyên: chỉ log tên kiểu, không log message

        await Results.Problem(
            statusCode: statusCode,
            title: ReasonPhrases.GetReasonPhrase(statusCode),
            detail: detail,
            extensions: new Dictionary<string, object?> { ["traceId"] = traceId })
            .ExecuteAsync(httpContext);
        return true;
    }

    // 34.3 Ánh xạ exception nghiệp vụ -> HTTP. THỨ TỰ QUAN TRỌNG: lớp con phải đứng trước DomainException
    private static (int StatusCode, string Detail) Classify(Exception exception) => exception switch
    {
        BadHttpRequestException { StatusCode: >= 400 and < 500 } bad =>
            (bad.StatusCode, "The request could not be processed. Check the request format and size."),
        InvalidPaymentStateException or IdempotencyConflictException or ConcurrentPaymentUpdateException =>
            (StatusCodes.Status409Conflict, exception.Message),
        DomainException => (StatusCodes.Status400BadRequest, exception.Message),
        PaymentNotFoundException => (StatusCodes.Status404NotFound, exception.Message),
        PaymentGatewayUnavailableException => (StatusCodes.Status503ServiceUnavailable, exception.Message),
        _ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred. Contact support with the traceId.")
    };
}