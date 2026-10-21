var builder = WebApplication.CreateBuilder(args);
var app = SwiftRide.ApiGateway.GatewayApplication.Build(builder);
app.Run();
