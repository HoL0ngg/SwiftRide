namespace SwiftRide.MatchingService.Api;

public static class MatchingEndpoints
{
    public static IEndpointRouteBuilder MapMatchingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // TODO: Implement the service use cases when the MVP domain is ready.
        endpoints.MapPut("/drivers/{id}/location", () => Results.StatusCode(StatusCodes.Status501NotImplemented));
        endpoints.MapPost("/match", () => Results.StatusCode(StatusCodes.Status501NotImplemented));
        endpoints.MapPost("/quote", () => Results.StatusCode(StatusCodes.Status501NotImplemented));
        return endpoints;
    }
}
