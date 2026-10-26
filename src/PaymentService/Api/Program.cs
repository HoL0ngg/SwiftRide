using System.Text.Json.Serialization;
using SwiftRide.PaymentService.Api;
using SwiftRide.PaymentService.Application;
using SwiftRide.PaymentService.Infrastructure;
using SwiftRide.PaymentService.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));   // 35.1 "method":"Card" thay vì số 1
builder.Services.AddPaymentApplication();                                 // 35.2 Composition root: nối Application + Infrastructure
builder.Services.AddInfrastructure(builder.Configuration);

var app = builder.Build();
app.UseExceptionHandler();
if (app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))        // 35.3 Mặc định tắt: để `dotnet ef` không đụng DB
    await app.Services.ApplyMigrationsAsync();
app.MapGet("/health", () => Results.Ok());
app.MapPaymentEndpoints();
app.Run();