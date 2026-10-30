using System.Diagnostics;
using System.Security.Claims;
using SwiftRide.MatchingService.Api.Observability;
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

        endpoints.MapGet("/locations/search", async (string query, IAddressSearch search, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(query) || query.Trim().Length is < 3 or > 200)
                return Results.Problem(statusCode: 400, title: "Search text must have 3 to 200 characters.");

            try
            {
                var results = await search.SearchAsync(query, ct);
                return Results.Ok(results);
            }
            catch (GeocodingUnavailableException ex)
            {
                var logger = loggerFactory.CreateLogger("SwiftRide.MatchingService.Geocoding");
                logger.LogError(ex, "Geocoding service unavailable for '{Query}'.", query);
                return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Geocoding service is unavailable.");
            }
            catch (Exception ex)
            {
                var logger = loggerFactory.CreateLogger("SwiftRide.MatchingService.Geocoding");
                logger.LogError(ex, "Error occurred while searching locations for '{Query}'.", query);
                return Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "An error occurred while searching locations.");
            }
        }).RequireAuthorization(policy => policy.RequireRole("rider", "driver"));

        var internalGroup = endpoints.MapGroup("").AddEndpointFilter(async (context, next) =>
        {
            var config = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
            var expectedKey = config["Security:InternalApiKey"] ?? config["Matching:InternalApiKey"] ?? config["InternalApiKey"];
            if (string.IsNullOrWhiteSpace(expectedKey))
            {
                return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Internal API key is not configured.");
            }

            if (!context.HttpContext.Request.Headers.TryGetValue("X-Internal-Api-Key", out var headerKey) ||
                headerKey != expectedKey)
            {
                return Results.StatusCode(StatusCodes.Status401Unauthorized);
            }

            return await next(context);
        });

        internalGroup.MapPost("/match", async (MatchRequest request, MatchDriver useCase, CancellationToken ct) =>
        {
            var start = Stopwatch.GetTimestamp();
            var result = await useCase.ExecuteAsync(new CoordinateRequest(request.Latitude, request.Longitude).ToPoint(), request.ExcludedDriverIds ?? [], ct);
            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            MatchingTelemetry.MatchingDuration.Record(elapsedMs);
            return Results.Ok(result);
        });

        internalGroup.MapPost("/quote", async (QuoteRequest request, CreateQuote useCase, CancellationToken ct) =>
        {
            if (request.Pickup is null || request.Destination is null) throw new MatchingDomainException("Pickup and destination are required.");
            var quote = await useCase.ExecuteAsync(request.Pickup.ToPoint(), request.Destination.ToPoint(), request.Mode ?? "standard", ct);
            return Results.Json(quote, statusCode: 201);
        });

        return endpoints;
    }
}
