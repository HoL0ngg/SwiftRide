using SwiftRide.TripService.Infrastructure;
using SwiftRide.TripService.Api;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddInfrastructure(builder.Configuration);

var app = builder.Build();
app.MapGet("/health", () => Results.Ok());
app.MapTripEndpoints();
app.Run();
