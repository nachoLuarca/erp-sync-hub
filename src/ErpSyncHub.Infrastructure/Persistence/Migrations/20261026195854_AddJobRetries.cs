using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ErpSyncHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddJobRetries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_sync_jobs_status",
                table: "sync_jobs");

            migrationBuilder.AddColumn<int>(
                name: "cycle_attempts",
                table: "sync_jobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Los jobs que ya existían conservan sus intentos: el ciclo actual es todo lo que llevan hasta ahora.
            migrationBuilder.Sql("UPDATE sync_jobs SET cycle_attempts = attempt_count");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "next_attempt_at",
                table: "sync_jobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_sync_jobs_retrying",
                table: "sync_jobs",
                column: "next_attempt_at",
                filter: "status = 'retrying'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_sync_jobs_status",
                table: "sync_jobs",
                sql: "status IN ('pending', 'processing', 'succeeded', 'retrying', 'failed', 'dead_letter')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_sync_jobs_retrying",
                table: "sync_jobs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_sync_jobs_status",
                table: "sync_jobs");

            migrationBuilder.DropColumn(
                name: "cycle_attempts",
                table: "sync_jobs");

            migrationBuilder.DropColumn(
                name: "next_attempt_at",
                table: "sync_jobs");

            migrationBuilder.AddCheckConstraint(
                name: "ck_sync_jobs_status",
                table: "sync_jobs",
                sql: "status IN ('pending', 'processing', 'succeeded', 'failed')");
        }
    }
}
