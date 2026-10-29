using ErpSyncHub.Infrastructure;
using ErpSyncHub.Infrastructure.Persistence;
using ErpSyncHub.IntegrationTests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace ErpSyncHub.IntegrationTests.Configuration;

/// <summary>Database__ConnectionString acepta el formato de Npgsql y el formato URL de PostgreSQL, y nunca revela la contraseña.</summary>
public class DatabaseConnectionStringTests
{
    private const string Password = "S3cr3t-Clave";

    [Fact]
    public void Una_cadena_en_formato_Npgsql_pasa_sin_cambios()
    {
        const string npgsql = "Host=localhost;Port=5433;Database=erpsynchub;Username=erpsynchub;Password=clave";

        PostgresConnectionString.Normalize(npgsql).ShouldBe(npgsql);
        PostgresConnectionString.IsUrl(npgsql).ShouldBeFalse();
    }

    [Theory]
    [InlineData("postgresql://usuario:clave@ep-frio-123.us-east-1.aws.neon.tech/neondb?sslmode=require&channel_binding=require")]
    [InlineData("postgres://usuario:clave@ep-frio-123.us-east-1.aws.neon.tech/neondb?sslmode=require&channel_binding=require")]
    [InlineData("POSTGRESQL://usuario:clave@ep-frio-123.us-east-1.aws.neon.tech/neondb?SSLMODE=Require&Channel_Binding=Require")]
    public void Una_URL_de_Neon_se_convierte_al_formato_Npgsql(string url)
    {
        var builder = new NpgsqlConnectionStringBuilder(PostgresConnectionString.Normalize(url));

        builder.Host.ShouldBe("ep-frio-123.us-east-1.aws.neon.tech");
        builder.Port.ShouldBe(5432);
        builder.Database.ShouldBe("neondb");
        builder.Username.ShouldBe("usuario");
        builder.Password.ShouldBe("clave");
        builder.SslMode.ShouldBe(SslMode.Require);
        builder.ChannelBinding.ShouldBe(ChannelBinding.Require);
    }

    [Fact]
    public void La_URL_admite_puerto_y_los_demas_parametros_soportados()
    {
        var url = "postgresql://u:p@localhost:5433/erpsynchub?sslmode=verify-full&connect_timeout=7&application_name=erp-sync-hub";

        var builder = new NpgsqlConnectionStringBuilder(PostgresConnectionString.Normalize(url));

        builder.Port.ShouldBe(5433);
        builder.SslMode.ShouldBe(SslMode.VerifyFull);
        builder.Timeout.ShouldBe(7);
        builder.ApplicationName.ShouldBe("erp-sync-hub");
    }

    [Fact]
    public void Usuario_y_clave_con_caracteres_especiales_se_decodifican()
    {
        var url = "postgresql://us%40er:p%40ss%3Aw%2Ford%3B%25@localhost/mi%20base";

        var builder = new NpgsqlConnectionStringBuilder(PostgresConnectionString.Normalize(url));

        builder.Username.ShouldBe("us@er");
        builder.Password.ShouldBe("p@ss:w/ord;%");
        builder.Database.ShouldBe("mi base");
    }

    [Fact]
    public void Una_clave_con_punto_y_coma_no_rompe_la_cadena_resultante()
    {
        var normalized = PostgresConnectionString.Normalize("postgresql://u:a%3Bb%3DHost%3Dotro@localhost/db");

        var builder = new NpgsqlConnectionStringBuilder(normalized);
        builder.Password.ShouldBe("a;b=Host=otro");
        builder.Host.ShouldBe("localhost");
    }

    [Fact]
    public void La_URL_sin_puerto_ni_base_ni_credenciales_se_acepta()
    {
        var builder = new NpgsqlConnectionStringBuilder(PostgresConnectionString.Normalize("postgresql://localhost"));

        builder.Host.ShouldBe("localhost");
        builder.Port.ShouldBe(5432);
        builder.Database.ShouldBeNull();
    }

    [Fact]
    public void Un_host_IPv6_se_convierte_sin_corchetes()
    {
        var builder = new NpgsqlConnectionStringBuilder(PostgresConnectionString.Normalize("postgresql://u:p@[::1]:5433/db"));

        builder.Host.ShouldBe("::1");
        builder.Port.ShouldBe(5433);
    }

    [Theory]
    [InlineData("postgresql://u:" + Password + "@host/db?sslmode=casi", "sslmode")]
    [InlineData("postgresql://u:" + Password + "@host/db?channel_binding=siempre", "channel_binding")]
    [InlineData("postgresql://u:" + Password + "@host/db?connect_timeout=abc", "connect_timeout")]
    [InlineData("postgresql://u:" + Password + "@host/db?options=-c%20x", "options")]
    [InlineData("postgresql://u:" + Password + "@/db", "host")]
    public void Una_URL_invalida_se_rechaza_con_un_mensaje_que_no_revela_la_clave(string url, string mention)
    {
        PostgresConnectionString.TryNormalize(url, out var normalized, out var error).ShouldBeFalse();

        normalized.ShouldBeNull();
        error.ShouldContain(mention);
        error.ShouldNotContain(Password);

        var thrown = Should.Throw<FormatException>(() => PostgresConnectionString.Normalize(url));
        thrown.Message.ShouldNotContain(Password);
    }

    [Fact]
    public void Al_iniciar_una_URL_se_entrega_ya_convertida_al_resto_del_codigo()
    {
        using var provider = BuildProvider($"postgresql://usuario:{Password}@ep-frio.neon.tech/neondb?sslmode=require");

        var connectionString = provider.GetRequiredService<IOptions<DatabaseOptions>>().Value.ConnectionString;

        PostgresConnectionString.IsUrl(connectionString).ShouldBeFalse();
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        builder.Host.ShouldBe("ep-frio.neon.tech");
        builder.Password.ShouldBe(Password);
        builder.SslMode.ShouldBe(SslMode.Require);
    }

    [Fact]
    public void Al_iniciar_una_URL_invalida_impide_arrancar_nombra_la_variable_y_no_revela_la_clave()
    {
        using var provider = BuildProvider($"postgresql://usuario:{Password}@ep-frio.neon.tech/neondb?sslmode=casi");

        var failure = Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IOptions<DatabaseOptions>>().Value);

        failure.Message.ShouldContain("Database__ConnectionString");
        failure.Message.ShouldContain("sslmode");
        failure.Message.ShouldNotContain(Password);
        failure.Message.ShouldNotContain("ep-frio");
    }

    private static ServiceProvider BuildProvider(string connectionString)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:ConnectionString"] = connectionString })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(configuration);
        return services.BuildServiceProvider();
    }
}

/// <summary>Con un PostgreSQL real: el hub arranca y se conecta usando la cadena de conexión en formato URL.</summary>
[Collection(PostgresCollectionDefinition.Name)]
public class DatabaseConnectionStringUrlTests(PostgresFixture postgres)
{
    [Fact]
    public async Task El_hub_se_conecta_a_PostgreSQL_con_la_cadena_en_formato_URL()
    {
        var npgsql = new NpgsqlConnectionStringBuilder(await postgres.CreateMigratedDatabaseAsync());
        var url = $"postgresql://{Uri.EscapeDataString(npgsql.Username!)}:{Uri.EscapeDataString(npgsql.Password!)}" +
                  $"@{npgsql.Host}:{npgsql.Port}/{Uri.EscapeDataString(npgsql.Database!)}?sslmode=disable";
        await using var factory = ApiFactory.Create(url);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        using var body = await WebhookHost.ReadJsonAsync(response);
        body.RootElement.GetProperty("checks").GetProperty("postgres").GetProperty("status").GetString().ShouldBe("Healthy");
    }
}
