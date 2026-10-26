using ErpSyncHub.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace ErpSyncHub.IntegrationTests.Persistence;

[Collection(PostgresCollectionDefinition.Name)]
public class MigrationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task La_migracion_inicial_se_aplica_sobre_una_base_vacia_y_crea_todas_las_tablas()
    {
        var connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using var context = PostgresFixture.CreateContext(connectionString);

        await context.Database.MigrateAsync();

        var tables = await QueryAsync<string>(
            connectionString,
            "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_name <> '__EFMigrationsHistory' ORDER BY table_name");
        tables.ShouldBe(
            ["customer_mappings", "inventory_sync_runs", "item_snapshots", "product_mappings", "sync_attempts", "sync_jobs"]);
    }

    [Fact]
    public async Task El_modelo_y_la_migracion_estan_sincronizados()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        await using var context = PostgresFixture.CreateContext(connectionString);

        context.Database.HasPendingModelChanges().ShouldBeFalse(
            "Hay cambios en el modelo sin migración: ejecute 'dotnet ef migrations add'.");
        (await context.Database.GetPendingMigrationsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task El_indice_unico_de_idempotencia_cubre_origen_id_externo_y_tipo()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();

        var definition = (await QueryAsync<string>(
            connectionString,
            "SELECT indexdef FROM pg_indexes WHERE indexname = 'ux_sync_jobs_source_external_id_type'")).ShouldHaveSingleItem();

        definition.ShouldContain("UNIQUE");
        definition.ShouldContain("(source, external_id, type)");
    }

    [Fact]
    public async Task Los_indices_de_la_cola_son_parciales()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();

        var indexes = await QueryAsync<string>(
            connectionString,
            "SELECT indexdef FROM pg_indexes WHERE indexname IN ('ix_sync_jobs_pending', 'ix_sync_jobs_processing') ORDER BY indexname");

        indexes.Count.ShouldBe(2);
        indexes[0].ShouldContain("WHERE");
        indexes[0].ShouldContain("pending");
        indexes[1].ShouldContain("processing");
    }

    [Fact]
    public async Task Las_columnas_usan_snake_case_y_el_payload_es_jsonb()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();

        var columns = await QueryAsync<string>(
            connectionString,
            "SELECT column_name || ':' || data_type FROM information_schema.columns WHERE table_name = 'sync_jobs' ORDER BY column_name");

        columns.ShouldContain("payload:jsonb");
        columns.ShouldContain("external_id:character varying");
        columns.ShouldContain("locked_until:timestamp with time zone");
        columns.ShouldContain("store_total:numeric");
        columns.ShouldContain("sap_doc_entry:integer");
    }

    [Fact]
    public async Task La_migracion_de_reintentos_conserva_los_intentos_de_los_jobs_existentes()
    {
        var connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using var context = PostgresFixture.CreateContext(connectionString);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync("20261026154452_AddInventorySync");
        await ExecuteAsync(
            connectionString,
            "INSERT INTO sync_jobs (id, source, external_id, type, payload, status, attempt_count, created_at, updated_at) " +
            "VALUES (gen_random_uuid(), 'woocommerce', '1', 'order', '{}', 'failed', 3, now(), now())");

        await migrator.MigrateAsync();

        (await QueryAsync<int>(connectionString, "SELECT cycle_attempts FROM sync_jobs")).ShouldHaveSingleItem().ShouldBe(3);
        (await QueryAsync<string>(connectionString, "SELECT status FROM sync_jobs")).ShouldHaveSingleItem().ShouldBe("failed");
    }

    [Theory]
    [InlineData("retrying", true)]
    [InlineData("dead_letter", true)]
    [InlineData("failed", true)]
    [InlineData("inventado", false)]
    public async Task La_restriccion_de_estado_acepta_los_estados_de_reintento_y_rechaza_los_desconocidos(string status, bool accepted)
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        var insert =
            "INSERT INTO sync_jobs (id, source, external_id, type, payload, status, attempt_count, created_at, updated_at) " +
            $"VALUES (gen_random_uuid(), 'woocommerce', '1', 'order', '{{}}', '{status}', 0, now(), now())";

        if (accepted)
        {
            await Should.NotThrowAsync(() => ExecuteAsync(connectionString, insert));
        }
        else
        {
            var failure = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(connectionString, insert));
            failure.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        }
    }

    [Fact]
    public async Task El_indice_de_reintentos_es_parcial_sobre_los_jobs_en_espera()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();

        var definition = (await QueryAsync<string>(
            connectionString,
            "SELECT indexdef FROM pg_indexes WHERE indexname = 'ix_sync_jobs_retrying'")).ShouldHaveSingleItem();

        definition.ShouldContain("next_attempt_at");
        definition.ShouldContain("retrying");
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<List<T>> QueryAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();

        var values = new List<T>();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetFieldValue<T>(0));
        }

        return values;
    }
}
