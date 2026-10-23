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
    public async Task Sin_MigrateOnStartup_la_Api_arranca_sin_tocar_el_esquema_y_el_health_avisa_que_faltan_migraciones()
    {
        var connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using var factory = ApiFactory.Create(connectionString, migrateOnStartup: false);

        using var client = factory.CreateClient();
        var response = await client.GetAsync("/health");

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync()).ShouldContain("migraciones");
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

    [Theory]
    [InlineData("Worker:Concurrency", "0", "Worker:Concurrency")]
    [InlineData("Worker:Concurrency", "99", "Worker:Concurrency")]
    [InlineData("Worker:PollInterval", "00:00:00", "Worker:PollInterval")]
    [InlineData("Worker:LeaseDuration", "00:00:10", "al menos 30 segundos")]
    public void Una_configuracion_invalida_del_worker_impide_arrancar(string key, string value, string expectedInMessage)
    {
        using var factory = ApiFactory.Create("Host=localhost", configure: builder => builder.UseSetting(key, value));

        var exception = Should.Throw<OptionsValidationException>(() => factory.CreateClient());

        exception.Message.ShouldContain(expectedInMessage);
    }

    [Fact]
    public void El_lease_debe_ser_mayor_que_el_tiempo_de_espera_de_las_llamadas_a_SAP()
    {
        using var factory = ApiFactory.Create(
            "Host=localhost",
            configure: builder =>
            {
                builder.UseSetting("SAP:RequestTimeout", "00:02:00");
                builder.UseSetting("Worker:LeaseDuration", "00:01:00");
            });

        var exception = Should.Throw<OptionsValidationException>(() => factory.CreateClient());

        exception.Message.ShouldContain("mayor que el tiempo de espera");
        exception.Message.ShouldContain("SAP:RequestTimeout");
    }

    [Fact]
    public void Un_lease_mayor_que_el_tiempo_de_espera_de_SAP_se_acepta()
    {
        using var factory = ApiFactory.Create(
            "Host=localhost",
            configure: builder =>
            {
                builder.UseSetting("SAP:RequestTimeout", "00:00:30");
                builder.UseSetting("Worker:LeaseDuration", "00:01:00");
            });

        Should.NotThrow(() => factory.CreateClient());
    }

    [Fact]
    public void Una_zona_horaria_inexistente_impide_arrancar()
    {
        using var factory = ApiFactory.Create(
            "Host=localhost",
            configure: builder => builder.UseSetting("Sync:WooCommerce:TimeZoneId", "Marte/Olympus"));

        var exception = Should.Throw<OptionsValidationException>(() => factory.CreateClient());

        exception.Message.ShouldContain("zona horaria");
    }

    [Fact]
    public void Un_formato_de_RUT_desconocido_impide_arrancar_y_dice_que_variable_corregir()
    {
        using var factory = ApiFactory.Create(
            "Host=localhost",
            configure: builder => builder.UseSetting("SAP:TaxIdFormat", "ConGuionBajo"));

        var exception = Should.Throw<Exception>(() => factory.CreateClient());

        exception.ToString().ShouldContain("TaxIdFormat");
    }

    [Theory]
    [InlineData("WithDots")]
    [InlineData("WithoutDots")]
    public void Los_dos_formatos_de_RUT_son_validos(string format)
    {
        using var factory = ApiFactory.Create(
            "Host=localhost",
            configure: builder => builder.UseSetting("SAP:TaxIdFormat", format));

        Should.NotThrow(() => factory.CreateClient());
    }

    [Fact]
    public async Task Con_Seq_inalcanzable_el_servicio_arranca_y_responde_igual()
    {
        // Puerto 9 (discard) en loopback: nadie escucha, así que cada envío a Seq falla.
        using var factory = ApiFactory.Create(
            "Host=localhost",
            configure: builder => builder.UseSetting("Seq:ServerUrl", "http://127.0.0.1:9"));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health", CancellationToken.None);

        response.StatusCode.ShouldNotBe(System.Net.HttpStatusCode.InternalServerError);
    }

    [Theory]
    [InlineData("seq:80")]
    [InlineData("ftp://seq")]
    public void Una_URL_de_Seq_invalida_impide_arrancar_y_dice_que_variable_corregir(string url)
    {
        using var factory = ApiFactory.Create(
            "Host=localhost",
            configure: builder => builder.UseSetting("Seq:ServerUrl", url));

        var exception = Should.Throw<Exception>(() => factory.CreateClient());

        exception.ToString().ShouldContain("Seq__ServerUrl");
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
