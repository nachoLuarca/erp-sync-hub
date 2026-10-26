using ErpSyncHub.Domain.Inventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpSyncHub.Infrastructure.Persistence.Configurations;

internal sealed class ItemSnapshotConfiguration : IEntityTypeConfiguration<ItemSnapshot>
{
    public void Configure(EntityTypeBuilder<ItemSnapshot> builder)
    {
        builder.ToTable("item_snapshots", table =>
            table.HasCheckConstraint("ck_item_snapshots_stock_quantity", "stock_quantity >= 0"));

        builder.HasKey(snapshot => snapshot.Id);
        builder.Property(snapshot => snapshot.Id).ValueGeneratedNever();

        builder.Property(snapshot => snapshot.Sku).HasMaxLength(ItemSnapshot.MaxSkuLength).IsRequired();
        builder.Property(snapshot => snapshot.ItemCode).HasMaxLength(ItemSnapshot.MaxItemCodeLength).IsRequired();
        builder.Property(snapshot => snapshot.Price).HasPrecision(18, 4);

        builder.HasIndex(snapshot => snapshot.Sku).IsUnique().HasDatabaseName("ux_item_snapshots_sku");
        builder.HasIndex(snapshot => snapshot.ItemCode).HasDatabaseName("ix_item_snapshots_item_code");
    }
}

internal sealed class InventorySyncRunConfiguration : IEntityTypeConfiguration<InventorySyncRun>
{
    public void Configure(EntityTypeBuilder<InventorySyncRun> builder)
    {
        builder.ToTable("inventory_sync_runs", table =>
        {
            table.HasCheckConstraint(
                "ck_inventory_sync_runs_trigger",
                $"trigger IN ({Quote(SnakeCaseEnumConverter<InventorySyncTrigger>.AllowedValues)})");
            table.HasCheckConstraint(
                "ck_inventory_sync_runs_status",
                $"status IN ({Quote(SnakeCaseEnumConverter<InventorySyncStatus>.AllowedValues)})");
            table.HasCheckConstraint(
                "ck_inventory_sync_runs_counts",
                "items_read >= 0 AND items_updated >= 0 AND items_unchanged >= 0 AND items_skipped >= 0 AND items_failed >= 0");
        });

        builder.HasKey(run => run.Id);
        builder.Property(run => run.Id).ValueGeneratedNever();

        builder.Property(run => run.Trigger)
            .HasConversion(new SnakeCaseEnumConverter<InventorySyncTrigger>())
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(run => run.Status)
            .HasConversion(new SnakeCaseEnumConverter<InventorySyncStatus>())
            .HasMaxLength(30)
            .IsRequired();

        builder.Property(run => run.Instance).HasMaxLength(InventorySyncRun.MaxInstanceLength).IsRequired();
        builder.Property(run => run.ErrorCode).HasMaxLength(InventorySyncRun.MaxErrorCodeLength);
        builder.Property(run => run.ErrorMessage).HasMaxLength(InventorySyncRun.MaxErrorMessageLength);

        builder.HasIndex(run => run.StartedAt).IsDescending().HasDatabaseName("ix_inventory_sync_runs_started_at");
    }

    private static string Quote(IEnumerable<string> values) => string.Join(", ", values.Select(value => $"'{value}'"));
}
