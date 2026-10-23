using ErpSyncHub.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;
using Npgsql;

namespace ErpSyncHub.IntegrationTests;

[Collection(PostgresCollectionDefinition.Name)]
public class ApiStartupTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Con_MigrateOnStartup_la_Api_crea_el_esquema_al_arrancar()
    {
        var connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using var factory = ApiWith(connectionString, migrateOnStartup: true);

        using var client = factory.CreateClient();
        var response = await client.GetAsync("/health");

        response.IsSuccessStatusCode.ShouldBeTrue();
        (await TableCountAsync(connectionString)).ShouldBe(5); // las 4 tablas más el historial de migraciones
    }

    [Fact]
    public async Task Sin_MigrateOnStartup_la_Api_arranca_sin_tocar_el_esquema()
    {
        var connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using var factory = ApiWith(connectionString, migrateOnStartup: false);

        using var client = factory.CreateClient();
        var response = await client.GetAsync("/health");

        response.IsSuccessStatusCode.ShouldBeTrue();
        (await TableCountAsync(connectionString)).ShouldBe(0);
    }

    [Fact]
    public async Task Migrar_dos_veces_es_inocuo()
    {
        var connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using (var first = ApiWith(connectionString, migrateOnStartup: true))
        {
            using var client = first.CreateClient();
        }

        await using var second = ApiWith(connectionString, migrateOnStartup: true);
        using var secondClient = second.CreateClient();

        (await TableCountAsync(connectionString)).ShouldBe(5);
    }

    [Fact]
    public void Sin_cadena_de_conexion_la_Api_no_arranca_y_dice_que_variable_falta()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(
            builder => builder.UseSetting("Database:ConnectionString", string.Empty));

        var exception = Should.Throw<OptionsValidationException>(() => factory.CreateClient());

        exception.Message.ShouldContain("Database__ConnectionString");
    }

    private static WebApplicationFactory<Program> ApiWith(string connectionString, bool migrateOnStartup) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Database:ConnectionString", connectionString);
            builder.UseSetting("Database:MigrateOnStartup", migrateOnStartup ? "true" : "false");
        });

    private static async Task<long> TableCountAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public'", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
