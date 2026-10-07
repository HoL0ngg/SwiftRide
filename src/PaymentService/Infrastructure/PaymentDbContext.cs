using Microsoft.EntityFrameworkCore;

namespace SwiftRide.PaymentService.Infrastructure;

public sealed class PaymentDbContext(DbContextOptions<PaymentDbContext> options) : DbContext(options)
{
}
