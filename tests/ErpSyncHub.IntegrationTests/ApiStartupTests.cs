using ErpSyncHub.IntegrationTests.Support;
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
        await using var factory = ApiFactory.Create(connectionString, migrateOnStartup: true);

        using var client = factory.CreateClient();
        var response = await client.GetAsync("/health");

        response.IsSuccessStatusCode.ShouldBeTrue();
        (await TableCountAsync(connectionString)).ShouldBe(5); // las 4 tablas más el historial de migraciones
    }

    [Fact]
    public async Task Sin_MigrateOnStartup_la_Api_arranca_sin_tocar_el_esquema()
    {
        var connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using var factory = ApiFactory.Create(connectionString, migrateOnStartup: false);

        using var client = factory.CreateClient();
        var response = await client.GetAsync("/health");

        response.IsSuccessStatusCode.ShouldBeTrue();
        (await TableCountAsync(connectionString)).ShouldBe(0);
    }

    [Fact]
    public async Task Migrar_dos_veces_es_inocuo()
    {
        var connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using (var first = ApiFactory.Create(connectionString, migrateOnStartup: true))
        {
            using var client = first.CreateClient();
        }

        await using var second = ApiFactory.Create(connectionString, migrateOnStartup: true);
        using var secondClient = second.CreateClient();

        (await TableCountAsync(connectionString)).ShouldBe(5);
    }

    [Fact]
    public void Sin_cadena_de_conexion_la_Api_no_arranca_y_dice_que_variable_falta()
    {
        using var factory = ApiFactory.Create(string.Empty);

        var exception = Should.Throw<OptionsValidationException>(() => factory.CreateClient());

        exception.Message.ShouldContain("Database__ConnectionString");
    }

    [Fact]
    public void Sin_secreto_de_webhook_la_Api_no_arranca_y_dice_que_variable_falta()
    {
        using var factory = ApiFactory.Create("Host=localhost", webhookSecret: null);

        var exception = Should.Throw<OptionsValidationException>(() => factory.CreateClient());

        exception.Message.ShouldContain("Webhooks__WooCommerce__Secret");
    }

    [Fact]
    public void Un_secreto_de_webhook_demasiado_corto_se_rechaza()
    {
        using var factory = ApiFactory.Create("Host=localhost", webhookSecret: "corto");

        var exception = Should.Throw<OptionsValidationException>(() => factory.CreateClient());

        exception.Message.ShouldContain("al menos 16 caracteres");
    }

    [Theory]
    [InlineData("100")]
    [InlineData("20000000")]
    public void Un_limite_de_cuerpo_fuera_de_rango_se_rechaza(string maxBodyBytes)
    {
        using var factory = ApiFactory.Create(
            "Host=localhost",
            configure: builder => builder.UseSetting("Webhooks:WooCommerce:MaxBodyBytes", maxBodyBytes));

        var exception = Should.Throw<Exception>(() => factory.CreateClient());

        exception.Message.ShouldContain("MaxBodyBytes");
    }

    private static async Task<long> TableCountAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public'", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
