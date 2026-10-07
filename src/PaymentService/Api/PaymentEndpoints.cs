namespace SwiftRide.PaymentService.Api;

public static class PaymentEndpoints
{
    public static IEndpointRouteBuilder MapPaymentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // TODO: Implement the service use cases when the MVP domain is ready.
        endpoints.MapPost("/payments", () => Results.StatusCode(StatusCodes.Status501NotImplemented));
        endpoints.MapPost("/payments/{id}/refund", () => Results.StatusCode(StatusCodes.Status501NotImplemented));
        return endpoints;
    }
}
