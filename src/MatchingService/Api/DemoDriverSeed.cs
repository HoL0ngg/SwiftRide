using SwiftRide.MatchingService.Application;

namespace SwiftRide.MatchingService.Api;

public sealed class DemoDriverSeed(IServiceScopeFactory scopeFactory) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var scope = scopeFactory.CreateScope();
        var update = scope.ServiceProvider.GetRequiredService<UpdateDriverLocation>();
        await update.ExecuteAsync("demo-driver", new(10.7769, 106.7009), true, stoppingToken);
        await update.ExecuteAsync("demo-driver-2", new(10.781, 106.695), true, stoppingToken);
    }
}
