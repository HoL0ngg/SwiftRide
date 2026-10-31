using SwiftRide.TripService.Api;

var builder = WebApplication.CreateBuilder(args);
var app = TripApplication.Build(builder);
await app.InitializeDatabaseAsync();
app.Run();

public partial class Program;
