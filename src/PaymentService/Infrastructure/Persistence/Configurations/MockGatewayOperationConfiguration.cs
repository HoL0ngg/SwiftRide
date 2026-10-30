using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SwiftRide.PaymentService.Infrastructure.Persistence.Configurations;

public sealed class MockGatewayOperationConfiguration : IEntityTypeConfiguration<MockGatewayOperationRecord>
{
    public void Configure(EntityTypeBuilder<MockGatewayOperationRecord> builder)
    {
        builder.ToTable("mock_gateway_operations", DbNames.Schema);
        builder.HasKey(m => m.Id);

        builder.Property(m => m.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(m => m.Operation).HasColumnName("operation").IsRequired();
        builder.Property(m => m.IdempotencyKey).HasColumnName("idempotency_key").IsRequired();
        builder.Property(m => m.RequestHash).HasColumnName("request_hash").IsRequired();
        builder.Property(m => m.Outcome).HasColumnName("outcome").IsRequired();
        builder.Property(m => m.Reference).HasColumnName("reference");
        builder.Property(m => m.FailureCode).HasColumnName("failure_code");
        builder.Property(m => m.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");

        builder.HasIndex(m => new { m.Operation, m.IdempotencyKey }).IsUnique();
    }
}
