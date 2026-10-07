using SwiftRide.PaymentService.Infrastructure;
using SwiftRide.PaymentService.Api;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddInfrastructure(builder.Configuration);

var app = builder.Build();
app.MapGet("/health", () => Results.Ok());
app.MapPaymentEndpoints();
app.Run();
