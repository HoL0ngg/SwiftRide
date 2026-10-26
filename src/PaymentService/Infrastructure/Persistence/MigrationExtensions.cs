using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
namespace SwiftRide.PaymentService.Infrastructure.Persistence;

public static class MigrationExtensions
{
    public static async Task ApplyMigrationsAsync(this IServiceProvider services)
    {
        using var scope = services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<PaymentDbContext>().Database.MigrateAsync();
        // 24.1 Tạo bảng từ migration
    }
}