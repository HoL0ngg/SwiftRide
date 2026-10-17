using SwiftRide.MatchingService.Infrastructure;
using SwiftRide.MatchingService.Api;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<SwiftRide.MatchingService.Api.GlobalExceptionHandler>();
builder.Services.AddInfrastructure(builder.Configuration);

var app = builder.Build();
app.UseExceptionHandler();
app.MapGet("/health", () => Results.Ok());
app.MapMatchingEndpoints();
app.Run();
