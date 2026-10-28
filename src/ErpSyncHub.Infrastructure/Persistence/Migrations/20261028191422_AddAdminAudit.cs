using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ErpSyncHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAdminAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "admin_audit",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    action = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    target = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    key_fingerprint = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    outcome = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_admin_audit", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_admin_audit_occurred_at",
                table: "admin_audit",
                column: "occurred_at");

            // Solo inserción: ni siquiera quien tenga acceso a la base de datos modifica o borra lo ya registrado sin quitar antes este disparador.
            migrationBuilder.Sql(
                """
                CREATE FUNCTION admin_audit_forbid_changes() RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION 'admin_audit es de solo inserción: no se puede modificar ni borrar (%)', TG_OP;
                END;
                $$ LANGUAGE plpgsql
                """);
            migrationBuilder.Sql(
                """
                CREATE TRIGGER trg_admin_audit_append_only BEFORE UPDATE OR DELETE ON admin_audit
                FOR EACH ROW EXECUTE FUNCTION admin_audit_forbid_changes()
                """);
            migrationBuilder.Sql(
                """
                CREATE TRIGGER trg_admin_audit_no_truncate BEFORE TRUNCATE ON admin_audit
                FOR EACH STATEMENT EXECUTE FUNCTION admin_audit_forbid_changes()
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER trg_admin_audit_no_truncate ON admin_audit");
            migrationBuilder.Sql("DROP TRIGGER trg_admin_audit_append_only ON admin_audit");
            migrationBuilder.Sql("DROP FUNCTION admin_audit_forbid_changes()");

            migrationBuilder.DropTable(
                name: "admin_audit");
        }
    }
}
