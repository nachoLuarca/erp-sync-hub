using ErpSyncHub.IntegrationTests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ErpSyncHub.IntegrationTests.Configuration;

/// <summary>El largo máximo de los mensajes externos se configura con una variable y se valida al arrancar.</summary>
[Collection(PostgresCollectionDefinition.Name)]
public class ExternalMessageConfigurationTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData("10")]
    [InlineData("5000")]
    public async Task Un_largo_fuera_de_rango_impide_arrancar_y_nombra_la_variable(string value)
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        await using var factory = ApiFactory.Create(connectionString, configure: builder => builder.UseSetting("Messages:ExternalMaxLength", value));

        var failure = Should.Throw<OptionsValidationException>(() => factory.CreateClient());

        failure.Message.ShouldContain("Messages__ExternalMaxLength");
    }

    [Fact]
    public async Task El_largo_configurado_llega_al_recorte_de_los_mensajes()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        await using var factory = ApiFactory.Create(connectionString, configure: builder => builder.UseSetting("Messages:ExternalMaxLength", "80"));
        using var client = factory.CreateClient();

        var sanitizer = factory.Services.GetRequiredService<ErpSyncHub.Application.Common.ExternalMessageSanitizer>();

        sanitizer.Sanitize(new string('y', 1000)).Length.ShouldBe(80);
    }
}
