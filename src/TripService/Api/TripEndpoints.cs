using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using SwiftRide.TripService.Api.Observability;
using SwiftRide.TripService.Application.Abstractions;
using SwiftRide.TripService.Application.Trips;

namespace SwiftRide.TripService.Api;

public static class TripEndpoints
{
    public static IEndpointRouteBuilder MapTripEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/trips").RequireAuthorization();

        // Rider endpoints
        group.MapPost("", CreateTripAsync)
            .RequireAuthorization(p => p.RequireRole("rider"));

        group.MapGet("/current", GetCurrentTripAsync)
            .RequireAuthorization(p => p.RequireRole("rider", "driver"));

        group.MapGet("/driver-feed", GetDriverFeedAsync)
            .RequireAuthorization(p => p.RequireRole("driver"));
        group.MapGet("/feed", GetDriverFeedAsync)
            .RequireAuthorization(p => p.RequireRole("driver"));

        group.MapGet("/history", GetHistoryAsync)
            .RequireAuthorization(p => p.RequireRole("rider", "driver"));

        group.MapGet("/{id:guid}", GetTripByIdAsync)
            .RequireAuthorization(p => p.RequireRole("rider", "driver"));

        group.MapPost("/{id:guid}/confirm-quote", ConfirmQuoteAsync)
            .RequireAuthorization(p => p.RequireRole("rider"));

        group.MapPost("/{id:guid}/cancel", CancelTripAsync)
            .RequireAuthorization(p => p.RequireRole("rider"));

        // Driver endpoints
        group.MapPost("/{id:guid}/driver-accept", DriverAcceptAsync)
            .RequireAuthorization(p => p.RequireRole("driver"));

        group.MapPost("/{id:guid}/driver-reject", DriverRejectAsync)
            .RequireAuthorization(p => p.RequireRole("driver"));

        group.MapPost("/{id:guid}/en-route", EnRouteAsync)
            .RequireAuthorization(p => p.RequireRole("driver"));

        group.MapPost("/{id:guid}/pickup", PickupAsync)
            .RequireAuthorization(p => p.RequireRole("driver"));

        group.MapPost("/{id:guid}/dropoff", DropoffAsync)
            .RequireAuthorization(p => p.RequireRole("driver"));

        return endpoints;
    }

    private static TripActor GetActor(ClaimsPrincipal user)
    {
        var id = user.FindFirstValue("sub") ?? user.FindFirstValue(ClaimTypes.NameIdentifier);
        var role = user.FindFirstValue("role") ?? user.FindFirstValue(ClaimTypes.Role);
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(role))
        {
            throw new BadHttpRequestException("Actor identity or role is missing from authentication token.", StatusCodes.Status401Unauthorized);
        }
        return new TripActor(id, role.ToLowerInvariant());
    }

    private static async Task<IResult> CreateTripAsync(
        [FromBody] CreateTripRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        ClaimsPrincipal user,
        CreateTripUseCase useCase,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Missing Idempotency Key",
                detail: "The 'Idempotency-Key' request header is required.");
        }

        if (request.Pickup is null || request.Destination is null)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid Route",
                detail: "Pickup and Destination coordinates are required.");
        }

        var actor = GetActor(user);
        var command = new CreateTripCommand(
            actor,
            idempotencyKey,
            new GeoCoordinate(request.Pickup.Latitude, request.Pickup.Longitude),
            request.PickupName,
            request.PickupAddress,
            new GeoCoordinate(request.Destination.Latitude, request.Destination.Longitude),
            request.DestinationName,
            request.DestinationAddress,
            request.PaymentMethod ?? "card",
            request.Mode ?? "standard",
            request.PaymentTransport);

        var result = await useCase.ExecuteAsync(command, ct);
        TripTelemetry.TripsCreated.Add(1);
        var response = TripResponseMapper.ToResponse(result.Trip);

        if (result.IsReplay)
        {
            return result.IsPendingWork
                ? Results.Accepted($"/trips/{result.Trip.Id}", response)
                : Results.Ok(response);
        }

        return result.IsPendingWork
            ? Results.Accepted($"/trips/{result.Trip.Id}", response)
            : Results.Created($"/trips/{result.Trip.Id}", response);
    }

    private static async Task<IResult> GetCurrentTripAsync(
        ClaimsPrincipal user,
        TripQueryService queryService,
        CancellationToken ct)
    {
        var actor = GetActor(user);
        var trip = await queryService.GetCurrentTripAsync(actor, ct);
        if (trip is null)
        {
            return Results.NoContent();
        }
        return Results.Ok(TripResponseMapper.ToResponse(trip));
    }

    private static async Task<IResult> GetDriverFeedAsync(
        ClaimsPrincipal user,
        TripQueryService queryService,
        CancellationToken ct)
    {
        var actor = GetActor(user);
        var feed = await queryService.GetDriverFeedAsync(actor, ct);
        var offered = feed.OfferedTrip is not null ? TripResponseMapper.ToResponse(feed.OfferedTrip) : null;
        var active = feed.ActiveTrip is not null ? TripResponseMapper.ToResponse(feed.ActiveTrip) : null;

        return Results.Ok(new DriverFeedApiResponse(offered, active));
    }

    private static async Task<IResult> GetHistoryAsync(
        [FromQuery] string? cursor,
        [FromQuery] int? limit,
        ClaimsPrincipal user,
        TripQueryService queryService,
        CancellationToken ct)
    {
        var actor = GetActor(user);
        var page = await queryService.GetHistoryAsync(actor, cursor, limit ?? 20, ct);
        var items = page.Items.Select(TripResponseMapper.ToResponse).ToList();

        return Results.Ok(new TripHistoryPageApiResponse(items, page.NextCursor));
    }

    private static async Task<IResult> GetTripByIdAsync(
        Guid id,
        ClaimsPrincipal user,
        TripQueryService queryService,
        CancellationToken ct)
    {
        var actor = GetActor(user);
        var trip = await queryService.GetTripByIdAsync(actor, id, ct);
        if (trip is null)
        {
            return Results.NotFound();
        }
        return Results.Ok(TripResponseMapper.ToResponse(trip));
    }

    private static async Task<IResult> ConfirmQuoteAsync(
        Guid id,
        [FromBody] ConfirmQuoteRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        ClaimsPrincipal user,
        TripCommandUseCase useCase,
        CancellationToken ct)
    {
        var key = idempotencyKey ?? $"confirm-{id:D}";
        var actor = GetActor(user);
        var outcome = await useCase.ExecuteAsync(actor, id, new ConfirmQuoteCommand(request.QuoteId), key, ct);
        return Results.Ok(TripResponseMapper.ToResponse(outcome.Trip));
    }

    private static async Task<IResult> CancelTripAsync(
        Guid id,
        [FromBody] CancelTripRequest? request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        ClaimsPrincipal user,
        TripCommandUseCase useCase,
        CancellationToken ct)
    {
        var key = idempotencyKey ?? $"cancel-{id:D}";
        var actor = GetActor(user);
        var outcome = await useCase.ExecuteAsync(actor, id, new CancelTripCommand(request?.Reason), key, ct);
        TripTelemetry.TripsFailed.Add(1);
        return Results.Ok(TripResponseMapper.ToResponse(outcome.Trip));
    }

    private static async Task<IResult> DriverAcceptAsync(
        Guid id,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        ClaimsPrincipal user,
        TripCommandUseCase useCase,
        CancellationToken ct)
    {
        var key = idempotencyKey ?? $"accept-{id:D}";
        var actor = GetActor(user);
        var outcome = await useCase.ExecuteAsync(actor, id, new DriverAcceptCommand(), key, ct);
        return Results.Ok(TripResponseMapper.ToResponse(outcome.Trip));
    }

    private static async Task<IResult> DriverRejectAsync(
        Guid id,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        ClaimsPrincipal user,
        TripCommandUseCase useCase,
        CancellationToken ct)
    {
        var key = idempotencyKey ?? $"reject-{id:D}";
        var actor = GetActor(user);
        var outcome = await useCase.ExecuteAsync(actor, id, new DriverRejectCommand(), key, ct);
        return Results.Ok(TripResponseMapper.ToResponse(outcome.Trip));
    }

    private static async Task<IResult> EnRouteAsync(
        Guid id,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        ClaimsPrincipal user,
        TripCommandUseCase useCase,
        CancellationToken ct)
    {
        var key = idempotencyKey ?? $"enroute-{id:D}";
        var actor = GetActor(user);
        var outcome = await useCase.ExecuteAsync(actor, id, new EnRouteCommand(), key, ct);
        return Results.Ok(TripResponseMapper.ToResponse(outcome.Trip));
    }

    private static async Task<IResult> PickupAsync(
        Guid id,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        ClaimsPrincipal user,
        TripCommandUseCase useCase,
        CancellationToken ct)
    {
        var key = idempotencyKey ?? $"pickup-{id:D}";
        var actor = GetActor(user);
        var outcome = await useCase.ExecuteAsync(actor, id, new PickupCommand(), key, ct);
        return Results.Ok(TripResponseMapper.ToResponse(outcome.Trip));
    }

    private static async Task<IResult> DropoffAsync(
        Guid id,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        ClaimsPrincipal user,
        TripCommandUseCase useCase,
        CancellationToken ct)
    {
        var key = idempotencyKey ?? $"dropoff-{id:D}";
        var actor = GetActor(user);
        var outcome = await useCase.ExecuteAsync(actor, id, new DropoffCommand(), key, ct);
        return Results.Ok(TripResponseMapper.ToResponse(outcome.Trip));
    }
}
