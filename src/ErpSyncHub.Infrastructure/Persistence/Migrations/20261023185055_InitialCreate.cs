using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ErpSyncHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "customer_mappings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    card_code = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_customer_mappings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "product_mappings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sku = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    item_code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_product_mappings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "sync_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    external_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    store_total = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    sap_total = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    sap_doc_entry = table.Column<int>(type: "integer", nullable: true),
                    sap_doc_num = table.Column<int>(type: "integer", nullable: true),
                    sap_card_code = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: true),
                    last_error_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    last_error_message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    locked_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sync_jobs", x => x.id);
                    table.CheckConstraint("ck_sync_jobs_status", "status IN ('pending', 'processing', 'succeeded', 'failed')");
                });

            migrationBuilder.CreateTable(
                name: "sync_attempts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    worker_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    error_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    error_message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    error_kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sync_attempts", x => x.id);
                    table.CheckConstraint("ck_sync_attempts_error_kind", "error_kind IS NULL OR error_kind IN ('validation', 'not_found', 'conflict', 'rejected', 'transient', 'unexpected')");
                    table.CheckConstraint("ck_sync_attempts_outcome", "outcome IN ('in_progress', 'succeeded', 'failed', 'abandoned')");
                    table.ForeignKey(
                        name: "fk_sync_attempts_sync_jobs_job_id",
                        column: x => x.job_id,
                        principalTable: "sync_jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_customer_mappings_external_key",
                table: "customer_mappings",
                column: "external_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_product_mappings_sku",
                table: "product_mappings",
                column: "sku",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_sync_attempts_job_id_number",
                table: "sync_attempts",
                columns: new[] { "job_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sync_jobs_pending",
                table: "sync_jobs",
                column: "created_at",
                filter: "status = 'pending'");

            migrationBuilder.CreateIndex(
                name: "ix_sync_jobs_processing",
                table: "sync_jobs",
                column: "locked_until",
                filter: "status = 'processing'");

            migrationBuilder.CreateIndex(
                name: "ux_sync_jobs_source_external_id_type",
                table: "sync_jobs",
                columns: new[] { "source", "external_id", "type" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "customer_mappings");

            migrationBuilder.DropTable(
                name: "product_mappings");

            migrationBuilder.DropTable(
                name: "sync_attempts");

            migrationBuilder.DropTable(
                name: "sync_jobs");
        }
    }
}
