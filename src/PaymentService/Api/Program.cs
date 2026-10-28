using System.Text.Json.Serialization;
using SwiftRide.PaymentService.Api;
using SwiftRide.PaymentService.Application;
using SwiftRide.PaymentService.Infrastructure;

DotNetEnv.Env.TraversePath().Load();   // tìm .env từ thư mục hiện tại đi lên, nạp thành biến môi trường

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));   // "method":"Card" thay vì số 1
builder.Services.AddPaymentApplication();                                 // Composition root: nối Application + Infrastructure
builder.Services.AddInfrastructure(builder.Configuration);

var app = builder.Build();
app.UseExceptionHandler();
app.MapGet("/health", () => Results.Ok());
app.MapPaymentEndpoints();
app.Run();

public partial class Program;   // cho integration test (WebApplicationFactory)
