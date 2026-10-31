using SwiftRide.PaymentService.Api;

var builder = WebApplication.CreateBuilder(args);
var app = PaymentApplication.Build(builder);
app.Run();

public partial class Program;
