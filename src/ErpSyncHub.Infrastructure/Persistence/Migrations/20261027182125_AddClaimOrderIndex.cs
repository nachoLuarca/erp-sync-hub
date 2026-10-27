using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ErpSyncHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddClaimOrderIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_sync_jobs_pending",
                table: "sync_jobs");

            migrationBuilder.DropIndex(
                name: "ix_sync_jobs_retrying",
                table: "sync_jobs");

            // Índice por expresión (EF Core no lo modela): el mismo orden con que PostgresJobQueue.ClaimAsync reparte los jobs.
            migrationBuilder.Sql(
                """
                CREATE INDEX ix_sync_jobs_claimable ON sync_jobs ((COALESCE(next_attempt_at, created_at)), id)
                WHERE status IN ('pending', 'retrying')
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX ix_sync_jobs_claimable");

            migrationBuilder.CreateIndex(
                name: "ix_sync_jobs_pending",
                table: "sync_jobs",
                column: "created_at",
                filter: "status = 'pending'");

            migrationBuilder.CreateIndex(
                name: "ix_sync_jobs_retrying",
                table: "sync_jobs",
                column: "next_attempt_at",
                filter: "status = 'retrying'");
        }
    }
}
