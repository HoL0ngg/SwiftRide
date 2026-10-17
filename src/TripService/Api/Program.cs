using SwiftRide.TripService.Infrastructure;
using SwiftRide.TripService.Api;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<SwiftRide.TripService.Api.GlobalExceptionHandler>();
builder.Services.AddInfrastructure(builder.Configuration);

var app = builder.Build();
app.UseExceptionHandler();
app.MapGet("/health", () => Results.Ok());
app.MapTripEndpoints();
app.Run();
