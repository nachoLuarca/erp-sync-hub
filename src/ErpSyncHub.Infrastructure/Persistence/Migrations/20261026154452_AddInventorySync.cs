using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ErpSyncHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInventorySync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "inventory_sync_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    trigger = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    instance = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    full = table.Column<bool>(type: "boolean", nullable: false),
                    changed_since = table.Column<DateOnly>(type: "date", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    items_read = table.Column<int>(type: "integer", nullable: false),
                    items_updated = table.Column<int>(type: "integer", nullable: false),
                    items_unchanged = table.Column<int>(type: "integer", nullable: false),
                    items_skipped = table.Column<int>(type: "integer", nullable: false),
                    items_failed = table.Column<int>(type: "integer", nullable: false),
                    error_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    error_message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_inventory_sync_runs", x => x.id);
                    table.CheckConstraint("ck_inventory_sync_runs_counts", "items_read >= 0 AND items_updated >= 0 AND items_unchanged >= 0 AND items_skipped >= 0 AND items_failed >= 0");
                    table.CheckConstraint("ck_inventory_sync_runs_status", "status IN ('running', 'succeeded', 'completed_with_errors', 'failed', 'abandoned')");
                    table.CheckConstraint("ck_inventory_sync_runs_trigger", "trigger IN ('scheduled', 'manual')");
                });

            migrationBuilder.CreateTable(
                name: "item_snapshots",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sku = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    item_code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    stock_quantity = table.Column<int>(type: "integer", nullable: false),
                    price = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    synced_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_item_snapshots", x => x.id);
                    table.CheckConstraint("ck_item_snapshots_stock_quantity", "stock_quantity >= 0");
                });

            migrationBuilder.CreateIndex(
                name: "ix_inventory_sync_runs_started_at",
                table: "inventory_sync_runs",
                column: "started_at",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_item_snapshots_item_code",
                table: "item_snapshots",
                column: "item_code");

            migrationBuilder.CreateIndex(
                name: "ux_item_snapshots_sku",
                table: "item_snapshots",
                column: "sku",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "inventory_sync_runs");

            migrationBuilder.DropTable(
                name: "item_snapshots");
        }
    }
}
