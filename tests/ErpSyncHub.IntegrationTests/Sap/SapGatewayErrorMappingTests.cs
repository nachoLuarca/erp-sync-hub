using System.Net;
using ErpSyncHub.Application.Common;
using ErpSyncHub.Application.Sap;
using ErpSyncHub.Domain.Common;
using ErpSyncHub.IntegrationTests.Support;
using NSubstitute.ExceptionExtensions;
using SapB1.Client;
using SapB1.Client.Models;

namespace ErpSyncHub.IntegrationTests.Sap;

/// <summary>
/// Cómo el adaptador clasifica cada fallo de SAP (ADR 0006): los definitivos no se reintentan y los pasajeros sí.
/// Se provoca cada excepción sustituyendo el cliente, sin red.
/// </summary>
public class SapGatewayErrorMappingTests
{
    private readonly ISapB1Client _client = Substitute.For<ISapB1Client>();
    private readonly IItemsApi _items = Substitute.For<IItemsApi>();

    public SapGatewayErrorMappingTests()
    {
        _client.Items.Returns(_items);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, -10, "Item 'X' does not exist")]
    [InlineData(HttpStatusCode.NotFound, -2028, "No matching records found")]
    [InlineData(HttpStatusCode.Forbidden, null, null)]
    [InlineData(HttpStatusCode.Conflict, 7, "choca")]
    public async Task Los_rechazos_4xx_son_definitivos_y_conservan_el_mensaje_de_SAP(HttpStatusCode status, int? code, string? message)
    {
        _items.GetAsync("A1", Arg.Any<CancellationToken>()).ThrowsAsync(new SapB1ApiException(status, code, message));

        var result = await Gateway().GetItemAsync("A1", CancellationToken.None);

        result.Error!.Kind.ShouldBe(ErrorKind.Rejected);
        result.Error.Code.ShouldBe(SapErrors.Rejected);
        result.Error.Message.ShouldContain($"HTTP {(int)status}");
        if (code is { } sapCode)
        {
            result.Error.Message.ShouldContain($"código {sapCode}");
        }

        if (message is not null)
        {
            result.Error.Message.ShouldContain(message);
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData((HttpStatusCode)425)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Los_errores_del_servidor_y_los_4xx_que_piden_esperar_son_pasajeros(HttpStatusCode status)
    {
        _items.GetAsync("A1", Arg.Any<CancellationToken>()).ThrowsAsync(new SapB1ApiException(status, null, "intente después"));

        var result = await Gateway().GetItemAsync("A1", CancellationToken.None);

        result.Error!.Kind.ShouldBe(ErrorKind.Transient);
        result.Error.IsTransient.ShouldBeTrue();
        result.Error.Code.ShouldBe(SapErrors.ServerError);
        result.Error.Message.ShouldContain($"HTTP {(int)status}");
    }

    [Fact]
    public async Task Una_respuesta_exitosa_que_no_se_pudo_leer_es_pasajera()
    {
        _items.GetAsync("A1", Arg.Any<CancellationToken>())
            .ThrowsAsync(new SapB1ApiException(HttpStatusCode.OK, null, "SAP returned a response that could not be read"));

        var result = await Gateway().GetItemAsync("A1", CancellationToken.None);

        result.Error!.Kind.ShouldBe(ErrorKind.Transient);
    }

    [Fact]
    public async Task Las_credenciales_rechazadas_en_el_inicio_de_sesion_tienen_su_propio_codigo_y_siguen_siendo_pasajeras()
    {
        _items.GetAsync("A1", Arg.Any<CancellationToken>())
            .ThrowsAsync(new SapB1SessionException("SAP login failed", HttpStatusCode.Unauthorized, -304, "Fail to get DB credentials"));

        var result = await Gateway().GetItemAsync("A1", CancellationToken.None);

        result.Error!.Kind.ShouldBe(ErrorKind.Transient);
        result.Error.Code.ShouldBe(SapErrors.InvalidCredentials);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, 301, "Invalid session.")]
    [InlineData(HttpStatusCode.BadGateway, null, null)]
    public async Task Los_problemas_de_sesion_son_pasajeros(HttpStatusCode status, int? code, string? message)
    {
        _items.GetAsync("A1", Arg.Any<CancellationToken>()).ThrowsAsync(new SapB1SessionException("SAP login failed", status, code, message));

        var result = await Gateway().GetItemAsync("A1", CancellationToken.None);

        result.Error!.Kind.ShouldBe(ErrorKind.Transient);
        result.Error.Code.ShouldBe(SapErrors.Session);
        result.Error.Message.ShouldContain($"HTTP {(int)status}");
    }

    [Fact]
    public async Task El_mensaje_de_SAP_no_deja_pasar_el_nombre_ni_la_direccion_ni_el_telefono_del_pedido_en_proceso()
    {
        _items.GetAsync("A1", Arg.Any<CancellationToken>()).ThrowsAsync(new SapB1ApiException(
            HttpStatusCode.BadRequest,
            -10,
            "Customer VALENTINA MUNOZ at av. providencia 1234 (+56 9 1234 5678) is frozen"));
        using var personalData = PersonalDataScope.Begin(
        [
            new PersonalValue(PersonalDataKind.Name, "Valentina Muñoz"),
            new PersonalValue(PersonalDataKind.Address, "Av. Providencia 1234"),
            new PersonalValue(PersonalDataKind.Phone, "+56912345678"),
        ]);

        var result = await Gateway().GetItemAsync("A1", CancellationToken.None);

        result.Error!.Message.ShouldContain("Customer [nombre] at [dirección] ([teléfono]) is frozen");
        result.Error.Message.ShouldNotContain("MUNOZ");
        result.Error.Message.ShouldNotContain("providencia");
        result.Error.Message.ShouldNotContain("5678");
    }

    [Fact]
    public async Task Sin_pedido_en_proceso_el_mensaje_de_SAP_conserva_los_nombres()
    {
        _items.GetAsync("A1", Arg.Any<CancellationToken>()).ThrowsAsync(new SapB1ApiException(HttpStatusCode.BadRequest, -10, "Customer VALENTINA MUNOZ is frozen"));

        var result = await Gateway().GetItemAsync("A1", CancellationToken.None);

        result.Error!.Message.ShouldContain("VALENTINA MUNOZ"); // el ámbito es del pedido en proceso, no una lista global
    }

    [Fact]
    public async Task Un_mensaje_enorme_de_SAP_se_recorta_al_largo_maximo()
    {
        _items.GetAsync("A1", Arg.Any<CancellationToken>()).ThrowsAsync(new SapB1ApiException(HttpStatusCode.BadRequest, -10, new string('x', 20_000)));

        var result = await Gateway().GetItemAsync("A1", CancellationToken.None);

        result.Error!.Message.Length.ShouldBeLessThan(500); // el mensaje de SAP queda en 300 caracteres más el encabezado
        result.Error.Message.ShouldEndWith("…");
    }

    [Theory]
    [InlineData("Business partner 'C18765432-7' does not exist", "Business partner 'C[RUT]' does not exist")]
    [InlineData("Duplicate FederalTaxID 18.765.432-7 for valentina@example.com", "Duplicate FederalTaxID [RUT] for [correo]")]
    public async Task El_mensaje_de_SAP_no_deja_pasar_el_RUT_ni_el_correo_del_cliente(string sapMessage, string expected)
    {
        _items.GetAsync("A1", Arg.Any<CancellationToken>()).ThrowsAsync(new SapB1ApiException(HttpStatusCode.BadRequest, -10, sapMessage));

        var result = await Gateway().GetItemAsync("A1", CancellationToken.None);

        result.Error!.Code.ShouldBe(SapErrors.Rejected);
        result.Error.Message.ShouldContain(expected);
        result.Error.Message.ShouldNotContain("18765432");
        result.Error.Message.ShouldNotContain("valentina");
    }

    [Fact]
    public async Task Una_conexion_cortada_a_mitad_de_la_respuesta_es_una_falla_de_red_pasajera()
    {
        _items.GetAsync("A1", Arg.Any<CancellationToken>()).ThrowsAsync(new IOException("Unexpected end of stream"));

        var result = await Gateway().GetItemAsync("A1", CancellationToken.None);

        result.Error!.Kind.ShouldBe(ErrorKind.Transient);
        result.Error.Code.ShouldBe(SapErrors.Unreachable);
        result.Error.Message.ShouldContain("Unexpected end of stream");
    }

    [Fact]
    public async Task Un_IOException_sin_mensaje_se_describe_por_su_tipo()
    {
        _items.GetAsync("A1", Arg.Any<CancellationToken>()).ThrowsAsync(new IOException(string.Empty));

        var result = await Gateway().GetItemAsync("A1", CancellationToken.None);

        result.Error!.Code.ShouldBe(SapErrors.Unreachable);
        result.Error.Message.ShouldContain(nameof(IOException));
    }

    [Fact]
    public async Task Una_falla_de_red_es_pasajera()
    {
        _items.GetAsync("A1", Arg.Any<CancellationToken>()).ThrowsAsync(new HttpRequestException("Connection refused"));

        var result = await Gateway().GetItemAsync("A1", CancellationToken.None);

        result.Error!.Kind.ShouldBe(ErrorKind.Transient);
        result.Error.Code.ShouldBe(SapErrors.Unreachable);
        result.Error.Message.ShouldContain("Connection refused");
    }

    [Fact]
    public async Task Un_tiempo_de_espera_agotado_es_pasajero()
    {
        _items.GetAsync("A1", Arg.Any<CancellationToken>())
            .ThrowsAsync(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout", new TimeoutException()));

        var result = await Gateway().GetItemAsync("A1", CancellationToken.None);

        result.Error!.Kind.ShouldBe(ErrorKind.Transient);
        result.Error.Code.ShouldBe(SapErrors.Timeout);
    }

    [Fact]
    public async Task La_cancelacion_pedida_por_el_servicio_no_se_convierte_en_error()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        _items.GetAsync("A1", Arg.Any<CancellationToken>()).ThrowsAsync(new OperationCanceledException(cts.Token));

        await Should.ThrowAsync<OperationCanceledException>(() => Gateway().GetItemAsync("A1", cts.Token));
    }

    [Fact]
    public async Task Un_defecto_inesperado_no_se_disfraza_de_error_de_SAP()
    {
        _items.GetAsync("A1", Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("bug"));

        await Should.ThrowAsync<InvalidOperationException>(() => Gateway().GetItemAsync("A1", CancellationToken.None));
    }

    [Fact]
    public async Task Si_SAP_dice_que_el_cliente_existe_pero_no_se_puede_leer_el_error_es_pasajero()
    {
        var partners = Substitute.For<IBusinessPartnersApi>();
        _client.BusinessPartners.Returns(partners);
        partners.CreateAsync(Arg.Any<CreateBusinessPartnerRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new SapB1ApiException(HttpStatusCode.BadRequest, SapB1ErrorCodes.AlreadyExists, "ya existe"));
        partners.GetAsync("WC1", Arg.Any<CancellationToken>()).Returns(Task.FromResult<BusinessPartner?>(null));

        var result = await Gateway().EnsureCustomerAsync(new NewSapCustomer("WC1", "Cliente", null, null, null, []), CancellationToken.None);

        result.Error!.Code.ShouldBe(SapErrors.CustomerConflict);
        result.Error.Kind.ShouldBe(ErrorKind.Transient);
    }

    [Fact]
    public async Task Si_leer_al_cliente_existente_falla_se_informa_ese_fallo()
    {
        var partners = Substitute.For<IBusinessPartnersApi>();
        _client.BusinessPartners.Returns(partners);
        partners.CreateAsync(Arg.Any<CreateBusinessPartnerRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new SapB1ApiException(HttpStatusCode.BadRequest, SapB1ErrorCodes.AlreadyExists, "ya existe"));
        partners.GetAsync("WC1", Arg.Any<CancellationToken>())
            .ThrowsAsync(new SapB1ApiException(HttpStatusCode.ServiceUnavailable, null, "caído"));

        var result = await Gateway().EnsureCustomerAsync(new NewSapCustomer("WC1", "Cliente", null, null, null, []), CancellationToken.None);

        result.Error!.Code.ShouldBe(SapErrors.ServerError);
    }

    [Fact]
    public async Task Un_error_distinto_de_ya_existe_al_crear_el_cliente_no_se_trata_como_existente()
    {
        var partners = Substitute.For<IBusinessPartnersApi>();
        _client.BusinessPartners.Returns(partners);
        partners.CreateAsync(Arg.Any<CreateBusinessPartnerRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new SapB1ApiException(HttpStatusCode.BadRequest, -10, "CardName requerido"));

        var result = await Gateway().EnsureCustomerAsync(new NewSapCustomer("WC1", "Cliente", null, null, null, []), CancellationToken.None);

        result.Error!.Kind.ShouldBe(ErrorKind.Rejected);
        await partners.DidNotReceive().GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private ISapGateway Gateway()
    {
        var harness = SapGatewayHarness.WithClient(_client);
        return harness.Gateway;
    }
}
