using ErpSyncHub.Domain.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpSyncHub.Infrastructure.Persistence.Configurations;

internal sealed class AdminAuditConfiguration : IEntityTypeConfiguration<AdminAuditEntry>
{
    public void Configure(EntityTypeBuilder<AdminAuditEntry> builder)
    {
        builder.ToTable("admin_audit");

        builder.HasKey(entry => entry.Id);
        builder.Property(entry => entry.Id).ValueGeneratedNever();

        builder.Property(entry => entry.Action).HasMaxLength(AdminAuditEntry.MaxActionLength).IsRequired();
        builder.Property(entry => entry.Target).HasMaxLength(AdminAuditEntry.MaxTargetLength);
        builder.Property(entry => entry.KeyFingerprint).HasMaxLength(AdminAuditEntry.MaxFingerprintLength).IsRequired();
        builder.Property(entry => entry.Role).HasMaxLength(AdminAuditEntry.MaxRoleLength).IsRequired();
        builder.Property(entry => entry.Outcome).HasMaxLength(AdminAuditEntry.MaxOutcomeLength).IsRequired();

        // Que la tabla sea de solo inserción lo garantiza un disparador (ver la
        // migración AddAdminAudit), que EF Core no modela.
        builder.HasIndex(entry => entry.OccurredAt).HasDatabaseName("ix_admin_audit_occurred_at");
    }
}
