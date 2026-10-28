using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ErpSyncHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCircuitBreakers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "circuit_breakers",
                columns: table => new
                {
                    name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    consecutive_failures = table.Column<int>(type: "integer", nullable: false),
                    first_failure_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reason = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    opened_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    open_count = table.Column<int>(type: "integer", nullable: false),
                    next_probe_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    probe_lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    last_error_message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_circuit_breakers", x => x.name);
                    table.CheckConstraint("ck_circuit_breakers_counters", "consecutive_failures >= 0 AND open_count >= 0");
                    table.CheckConstraint("ck_circuit_breakers_state", "state IN ('closed', 'open', 'half_open')");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "circuit_breakers");
        }
    }
}
