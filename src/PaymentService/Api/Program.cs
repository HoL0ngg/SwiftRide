using SwiftRide.PaymentService.Infrastructure;
using SwiftRide.PaymentService.Api;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<SwiftRide.PaymentService.Api.GlobalExceptionHandler>();
builder.Services.AddInfrastructure(builder.Configuration);

var app = builder.Build();
app.UseExceptionHandler();
app.MapGet("/health", () => Results.Ok());
app.MapPaymentEndpoints();
app.Run();
