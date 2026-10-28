using System.Security.Claims;
using SwiftRide.MatchingService.Application;
using SwiftRide.MatchingService.Application.Geocoding;
using SwiftRide.MatchingService.Domain;

namespace SwiftRide.MatchingService.Api;

public sealed record CoordinateRequest(double? Latitude, double? Longitude)
{
    public GeoPoint ToPoint() => Latitude.HasValue && Longitude.HasValue
        ? new(Latitude.Value, Longitude.Value) : throw new MatchingDomainException("Latitude and longitude are required.");
}
public sealed record LocationRequest(double? Latitude, double? Longitude, bool? IsOnline);
public sealed record MatchRequest(double? Latitude, double? Longitude, string[]? ExcludedDriverIds);
public sealed record QuoteRequest(CoordinateRequest? Pickup, CoordinateRequest? Destination, string? Mode);

public static class MatchingEndpoints
{
    public static IEndpointRouteBuilder MapMatchingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPut("/drivers/{id}/location", async (string id, LocationRequest request, ClaimsPrincipal user, UpdateDriverLocation useCase, CancellationToken ct) =>
        {
            if (user.FindFirstValue("sub") != id) return Results.Forbid();
            if (!request.IsOnline.HasValue) throw new MatchingDomainException("Online status is required.");
            await useCase.ExecuteAsync(id, new CoordinateRequest(request.Latitude, request.Longitude).ToPoint(), request.IsOnline.Value, ct);
            return Results.NoContent();
        }).RequireAuthorization(policy => policy.RequireRole("driver"));
        endpoints.MapGet("/locations/search", async (string query, IAddressSearch search, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(query) || query.Trim().Length is < 3 or > 200)
                return Results.Problem(statusCode: 400, title: "Search text must have 3 to 200 characters.");
            return Results.Ok(await search.SearchAsync(query, ct));
        }).RequireAuthorization(policy => policy.RequireRole("rider", "driver"));
        endpoints.MapPost("/match", async (MatchRequest request, MatchDriver useCase, CancellationToken ct) =>
            Results.Ok(await useCase.ExecuteAsync(new CoordinateRequest(request.Latitude, request.Longitude).ToPoint(), request.ExcludedDriverIds ?? [], ct)));
        endpoints.MapPost("/quote", async (QuoteRequest request, CreateQuote useCase, CancellationToken ct) =>
        {
            if (request.Pickup is null || request.Destination is null) throw new MatchingDomainException("Pickup and destination are required.");
            var quote = await useCase.ExecuteAsync(request.Pickup.ToPoint(), request.Destination.ToPoint(), request.Mode ?? "standard", ct);
            return Results.Json(quote, statusCode: 201);
        });
        return endpoints;
    }
}
