using SwiftRide.MatchingService.Infrastructure;
using SwiftRide.MatchingService.Api;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddInfrastructure(builder.Configuration);

var app = builder.Build();
app.MapGet("/health", () => Results.Ok());
app.MapMatchingEndpoints();
app.Run();
