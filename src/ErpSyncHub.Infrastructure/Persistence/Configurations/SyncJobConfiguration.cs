using ErpSyncHub.Domain.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpSyncHub.Infrastructure.Persistence.Configurations;

internal sealed class SyncJobConfiguration : IEntityTypeConfiguration<SyncJob>
{
    public void Configure(EntityTypeBuilder<SyncJob> builder)
    {
        builder.ToTable("sync_jobs", table => table.HasCheckConstraint(
            "ck_sync_jobs_status",
            $"status IN ({string.Join(", ", SnakeCaseEnumConverter<JobStatus>.AllowedValues.Select(value => $"'{value}'"))})"));

        builder.HasKey(job => job.Id);
        builder.Property(job => job.Id).ValueGeneratedNever();

        builder.Property(job => job.Source).HasMaxLength(SyncJob.MaxSourceLength).IsRequired();
        builder.Property(job => job.ExternalId).HasMaxLength(SyncJob.MaxExternalIdLength).IsRequired();
        builder.Property(job => job.Type).HasMaxLength(SyncJob.MaxTypeLength).IsRequired();
        builder.Property(job => job.Payload).HasColumnType("jsonb").IsRequired();

        builder.Property(job => job.Status)
            .HasConversion(new SnakeCaseEnumConverter<JobStatus>())
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(job => job.CycleAttempts).HasDefaultValue(0);
        builder.Property(job => job.StoreTotal).HasPrecision(18, 4);
        builder.Property(job => job.SapTotal).HasPrecision(18, 4);
        builder.Property(job => job.SapCardCode).HasMaxLength(Domain.Customers.SapCardCodes.MaxLength);
        builder.Property(job => job.LastErrorCode).HasMaxLength(SyncJob.MaxErrorCodeLength);
        builder.Property(job => job.LastErrorMessage).HasMaxLength(SyncJob.MaxErrorMessageLength);
        builder.Property(job => job.LockedBy).HasMaxLength(SyncJob.MaxWorkerIdLength);

        // Detecta que otro worker retomó el job mientras este terminaba: complementa al lease y a FOR UPDATE SKIP LOCKED.
        builder.Property<uint>("xmin")
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();

        builder.Ignore(job => job.HasTotalMismatch);

        // Idempotencia de la recepción: el mismo objeto del mismo origen y tipo se guarda una sola vez.
        builder.HasIndex(job => new { job.Source, job.ExternalId, job.Type })
            .IsUnique()
            .HasDatabaseName("ux_sync_jobs_source_external_id_type");

        // Índices parciales: la cola solo consulta lo pendiente y lo que tiene lease, no millones de jobs terminados.
        builder.HasIndex(job => job.CreatedAt)
            .HasFilter("status = 'pending'")
            .HasDatabaseName("ix_sync_jobs_pending");

        builder.HasIndex(job => job.LockedUntil)
            .HasFilter("status = 'processing'")
            .HasDatabaseName("ix_sync_jobs_processing");

        builder.HasIndex(job => job.NextAttemptAt)
            .HasFilter("status = 'retrying'")
            .HasDatabaseName("ix_sync_jobs_retrying");
    }
}
