using ErpSyncHub.Domain.Resilience;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpSyncHub.Infrastructure.Persistence.Configurations;

internal sealed class CircuitBreakerConfiguration : IEntityTypeConfiguration<CircuitBreaker>
{
    public void Configure(EntityTypeBuilder<CircuitBreaker> builder)
    {
        builder.ToTable("circuit_breakers", table =>
        {
            table.HasCheckConstraint("ck_circuit_breakers_state", $"state IN ({Quote(SnakeCaseEnumConverter<CircuitState>.AllowedValues)})");
            table.HasCheckConstraint("ck_circuit_breakers_counters", "consecutive_failures >= 0 AND open_count >= 0");
        });

        builder.HasKey(circuit => circuit.Name);
        builder.Property(circuit => circuit.Name).HasMaxLength(CircuitBreaker.MaxNameLength).ValueGeneratedNever();

        builder.Property(circuit => circuit.State)
            .HasConversion(new SnakeCaseEnumConverter<CircuitState>())
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(circuit => circuit.Reason)
            .HasConversion(new SnakeCaseEnumConverter<CircuitOpenReason>())
            .HasMaxLength(30);

        builder.Property(circuit => circuit.LastErrorCode).HasMaxLength(CircuitBreaker.MaxErrorCodeLength);
        builder.Property(circuit => circuit.LastErrorMessage).HasMaxLength(CircuitBreaker.MaxErrorMessageLength);
    }

    private static string Quote(IEnumerable<string> values) => string.Join(", ", values.Select(value => $"'{value}'"));
}
