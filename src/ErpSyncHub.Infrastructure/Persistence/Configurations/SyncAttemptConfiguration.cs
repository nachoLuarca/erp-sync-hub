using ErpSyncHub.Domain.Common;
using ErpSyncHub.Domain.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpSyncHub.Infrastructure.Persistence.Configurations;

internal sealed class SyncAttemptConfiguration : IEntityTypeConfiguration<SyncAttempt>
{
    public void Configure(EntityTypeBuilder<SyncAttempt> builder)
    {
        builder.ToTable("sync_attempts", table =>
        {
            table.HasCheckConstraint(
                "ck_sync_attempts_outcome",
                $"outcome IN ({Quote(SnakeCaseEnumConverter<AttemptOutcome>.AllowedValues)})");
            table.HasCheckConstraint(
                "ck_sync_attempts_error_kind",
                $"error_kind IS NULL OR error_kind IN ({Quote(SnakeCaseEnumConverter<ErrorKind>.AllowedValues)})");
        });

        builder.HasKey(attempt => attempt.Id);
        builder.Property(attempt => attempt.Id).ValueGeneratedNever();

        builder.Property(attempt => attempt.WorkerId).HasMaxLength(SyncJob.MaxWorkerIdLength).IsRequired();
        builder.Property(attempt => attempt.ErrorCode).HasMaxLength(SyncJob.MaxErrorCodeLength);
        builder.Property(attempt => attempt.ErrorMessage).HasMaxLength(SyncJob.MaxErrorMessageLength);

        builder.Property(attempt => attempt.Outcome)
            .HasConversion(new SnakeCaseEnumConverter<AttemptOutcome>())
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(attempt => attempt.ErrorKind)
            .HasConversion(new SnakeCaseEnumConverter<ErrorKind>())
            .HasMaxLength(20);

        builder.HasOne<SyncJob>()
            .WithMany()
            .HasForeignKey(attempt => attempt.JobId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(attempt => new { attempt.JobId, attempt.Number })
            .IsUnique()
            .HasDatabaseName("ux_sync_attempts_job_id_number");
    }

    private static string Quote(IEnumerable<string> values) => string.Join(", ", values.Select(value => $"'{value}'"));
}
