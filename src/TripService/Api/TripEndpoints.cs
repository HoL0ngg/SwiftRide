namespace SwiftRide.TripService.Api;

public static class TripEndpoints
{
    public static IEndpointRouteBuilder MapTripEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // TODO: Implement the service use cases when the MVP domain is ready.
        endpoints.MapPost("/trips", () => Results.StatusCode(StatusCodes.Status501NotImplemented));
        endpoints.MapGet("/trips/{id}", () => Results.StatusCode(StatusCodes.Status501NotImplemented));
        endpoints.MapPost("/trips/{id}/confirm-quote", () => Results.StatusCode(StatusCodes.Status501NotImplemented));
        endpoints.MapPost("/trips/{id}/cancel", () => Results.StatusCode(StatusCodes.Status501NotImplemented));
        endpoints.MapPost("/trips/{id}/driver-accept", () => Results.StatusCode(StatusCodes.Status501NotImplemented));
        endpoints.MapPost("/trips/{id}/driver-reject", () => Results.StatusCode(StatusCodes.Status501NotImplemented));
        endpoints.MapPost("/trips/{id}/en-route", () => Results.StatusCode(StatusCodes.Status501NotImplemented));
        endpoints.MapPost("/trips/{id}/pickup", () => Results.StatusCode(StatusCodes.Status501NotImplemented));
        endpoints.MapPost("/trips/{id}/dropoff", () => Results.StatusCode(StatusCodes.Status501NotImplemented));
        return endpoints;
    }
}
