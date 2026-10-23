using ErpSyncHub.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ErpSyncHub.IntegrationTests.Persistence;

[Collection(PostgresCollectionDefinition.Name)]
public class MigrationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task La_migracion_inicial_se_aplica_sobre_una_base_vacia_y_crea_las_cuatro_tablas()
    {
        var connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using var context = PostgresFixture.CreateContext(connectionString);

        await context.Database.MigrateAsync();

        var tables = await QueryAsync<string>(
            connectionString,
            "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_name <> '__EFMigrationsHistory' ORDER BY table_name");
        tables.ShouldBe(["customer_mappings", "product_mappings", "sync_attempts", "sync_jobs"]);
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
