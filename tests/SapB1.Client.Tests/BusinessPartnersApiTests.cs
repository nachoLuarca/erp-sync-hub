using System.Net;
using SapB1.Client.Models;
using SapB1.Client.Tests.Support;

namespace SapB1.Client.Tests;

public class BusinessPartnersApiTests
{
    [Fact]
    public async Task GetAsync_devuelve_el_socio_con_sus_direcciones()
    {
        await using var harness = ClientHarness.AgainstMock();

        var partner = await harness.Client.BusinessPartners.GetAsync("C00001");

        partner.ShouldNotBeNull();
        partner.CardCode.ShouldBe("C00001");
        partner.CardName.ShouldBe("Constructora Los Andes SpA");
        partner.CardType.ShouldBe(CardTypes.Customer);
        partner.FederalTaxID.ShouldBe("76.123.456-7");
        partner.EmailAddress.ShouldBe("compras@losandes.example.com");
        partner.Valid.ShouldBe(YesNo.Yes);
        partner.Frozen.ShouldBe(YesNo.No);
        partner.BPAddresses.Count.ShouldBe(2);
        partner.BPAddresses[0].ShouldBe(new BpAddress
        {
            AddressName = "Facturación",
            AddressType = AddressTypes.BillTo,
            Street = "Av. Providencia 1234",
            City = "Providencia",
            Country = "CL",
        });
    }

    [Fact]
    public async Task GetAsync_de_un_socio_inexistente_devuelve_null()
    {
        await using var harness = ClientHarness.AgainstMock();

        (await harness.Client.BusinessPartners.GetAsync("NO-EXISTE")).ShouldBeNull();
    }

    [Theory]
    [InlineData("O'BRIEN")]
    [InlineData("A B#1&?")]
    [InlineData("ñandú ü")]
    public async Task Las_claves_con_caracteres_especiales_llegan_intactas_a_SAP(string cardCode)
    {
        await using var harness = ClientHarness.AgainstMock();
        await harness.Client.BusinessPartners.CreateAsync(new CreateBusinessPartnerRequest { CardCode = cardCode, CardName = "Especial" });

        var partner = await harness.Client.BusinessPartners.GetAsync(cardCode);

        partner.ShouldNotBeNull().CardCode.ShouldBe(cardCode);
    }

    [Fact]
    public async Task QueryAsync_permite_buscar_por_RUT()
    {
        await using var harness = ClientHarness.AgainstMock();

        var found = await FirstAsync(harness, SapFilter.Equal("FederalTaxID", "12.345.678-5"));

        found.ShouldNotBeNull().CardCode.ShouldBe("C00003");
    }

    [Fact]
    public async Task QueryAsync_permite_combinar_condiciones()
    {
        await using var harness = ClientHarness.AgainstMock();
        var filter = SapFilter.And(
            SapFilter.Equal("CardType", CardTypes.Customer),
            "contains(CardName,'Ltda')");

        var codes = new List<string>();
        await foreach (var partner in harness.Client.BusinessPartners.QueryAsync(new SapQuery { Filter = filter, Select = ["CardCode"] }))
        {
            codes.Add(partner.CardCode);
        }

        codes.ShouldBe(["C00002", "C00006"]);
    }

    [Fact]
    public async Task QueryAsync_sin_coincidencias_devuelve_null_al_buscar_el_primero()
    {
        await using var harness = ClientHarness.AgainstMock();

        (await FirstAsync(harness, SapFilter.Equal("FederalTaxID", "00.000.000-0"))).ShouldBeNull();
    }

    [Fact]
    public async Task CreateAsync_con_solo_el_nombre_obtiene_el_codigo_de_la_serie_y_valores_por_defecto()
    {
        await using var harness = ClientHarness.AgainstMock();

        var created = await harness.Client.BusinessPartners.CreateAsync(new CreateBusinessPartnerRequest { CardName = "Cliente Nuevo" });

        created.CardCode.ShouldBe("C00007");
        created.CardName.ShouldBe("Cliente Nuevo");
        created.CardType.ShouldBe(CardTypes.Customer);
        created.Valid.ShouldBe(YesNo.Yes);
        created.Frozen.ShouldBe(YesNo.No);
        var stored = await harness.Client.BusinessPartners.GetAsync("C00007");
        stored.ShouldNotBeNull();
        (stored with { BPAddresses = [] }).ShouldBe(created with { BPAddresses = [] });
    }

    [Fact]
    public async Task CreateAsync_con_todos_los_datos_los_conserva()
    {
        await using var harness = ClientHarness.AgainstMock();

        var created = await harness.Client.BusinessPartners.CreateAsync(new CreateBusinessPartnerRequest
        {
            CardCode = "WEB-1001",
            CardName = "Pedro Pérez",
            CardType = CardTypes.Customer,
            FederalTaxID = "11.111.111-1",
            EmailAddress = "pedro@example.com",
            Phone1 = "+56 9 1111 2222",
            BPAddresses =
            [
                new BpAddress { AddressName = "Casa", AddressType = AddressTypes.ShipTo, Street = "Calle 1", City = "Santiago", Country = "CL" },
            ],
        });

        created.CardCode.ShouldBe("WEB-1001");
        created.FederalTaxID.ShouldBe("11.111.111-1");
        created.EmailAddress.ShouldBe("pedro@example.com");
        created.Phone1.ShouldBe("+56 9 1111 2222");
        created.BPAddresses.ShouldHaveSingleItem().Street.ShouldBe("Calle 1");
    }

    [Fact]
    public async Task CreateAsync_con_un_codigo_repetido_lanza_SapB1ApiException_IsAlreadyExists()
    {
        await using var harness = ClientHarness.AgainstMock();

        var exception = await Should.ThrowAsync<SapB1ApiException>(
            () => harness.Client.BusinessPartners.CreateAsync(new CreateBusinessPartnerRequest { CardCode = "C00001", CardName = "Repetido" }));

        exception.IsAlreadyExists.ShouldBeTrue();
        exception.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        exception.SapErrorCode.ShouldBe(SapB1ErrorCodes.AlreadyExists);
        exception.SapErrorMessage.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task CreateAsync_con_datos_invalidos_lanza_SapB1ApiException_con_el_motivo()
    {
        await using var harness = ClientHarness.AgainstMock();

        var exception = await Should.ThrowAsync<SapB1ApiException>(
            () => harness.Client.BusinessPartners.CreateAsync(new CreateBusinessPartnerRequest { CardName = "  " }));

        exception.SapErrorCode.ShouldBe(-10);
        exception.SapErrorMessage.ShouldNotBeNull().ShouldContain("CardName");
        exception.IsAlreadyExists.ShouldBeFalse();
        exception.IsNotFound.ShouldBeFalse();
    }

    [Fact]
    public async Task CreateAsync_rechaza_una_solicitud_nula()
    {
        await using var harness = ClientHarness.AgainstMock();

        await Should.ThrowAsync<ArgumentNullException>(() => harness.Client.BusinessPartners.CreateAsync(null!));
    }

    [Fact]
    public async Task UpdateAsync_modifica_solo_lo_enviado()
    {
        await using var harness = ClientHarness.AgainstMock();

        await harness.Client.BusinessPartners.UpdateAsync(
            "C00001",
            new UpdateBusinessPartnerRequest { EmailAddress = "nuevo@losandes.example.com", Frozen = YesNo.Yes });

        var partner = await harness.Client.BusinessPartners.GetAsync("C00001");
        partner.ShouldNotBeNull();
        partner.EmailAddress.ShouldBe("nuevo@losandes.example.com");
        partner.Frozen.ShouldBe(YesNo.Yes);
        partner.CardName.ShouldBe("Constructora Los Andes SpA");
        partner.FederalTaxID.ShouldBe("76.123.456-7");
        partner.BPAddresses.Count.ShouldBe(2);
    }

    [Fact]
    public async Task UpdateAsync_reemplaza_las_direcciones()
    {
        await using var harness = ClientHarness.AgainstMock();

        await harness.Client.BusinessPartners.UpdateAsync(
            "C00001",
            new UpdateBusinessPartnerRequest
            {
                BPAddresses = [new BpAddress { AddressName = "Nueva", AddressType = AddressTypes.ShipTo, City = "Rancagua" }],
            });

        var partner = await harness.Client.BusinessPartners.GetAsync("C00001");
        partner.ShouldNotBeNull().BPAddresses.ShouldHaveSingleItem().City.ShouldBe("Rancagua");
    }

    [Fact]
    public async Task UpdateAsync_sin_cambios_no_falla()
    {
        await using var harness = ClientHarness.AgainstMock();

        await harness.Client.BusinessPartners.UpdateAsync("C00001", new UpdateBusinessPartnerRequest());

        (await harness.Client.BusinessPartners.GetAsync("C00001")).ShouldNotBeNull().CardName.ShouldBe("Constructora Los Andes SpA");
    }

    [Fact]
    public async Task UpdateAsync_de_un_socio_inexistente_lanza_SapB1ApiException_IsNotFound()
    {
        await using var harness = ClientHarness.AgainstMock();

        var exception = await Should.ThrowAsync<SapB1ApiException>(
            () => harness.Client.BusinessPartners.UpdateAsync("NO-EXISTE", new UpdateBusinessPartnerRequest { CardName = "X" }));

        exception.IsNotFound.ShouldBeTrue();
        exception.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UpdateAsync_invalido_lanza_SapB1ApiException_y_no_modifica_el_socio()
    {
        await using var harness = ClientHarness.AgainstMock();

        var exception = await Should.ThrowAsync<SapB1ApiException>(
            () => harness.Client.BusinessPartners.UpdateAsync("C00001", new UpdateBusinessPartnerRequest { Frozen = "si" }));

        exception.SapErrorCode.ShouldBe(-10);
        (await harness.Client.BusinessPartners.GetAsync("C00001")).ShouldNotBeNull().Frozen.ShouldBe(YesNo.No);
    }

    [Fact]
    public async Task UpdateAsync_rechaza_una_solicitud_nula()
    {
        await using var harness = ClientHarness.AgainstMock();

        await Should.ThrowAsync<ArgumentNullException>(() => harness.Client.BusinessPartners.UpdateAsync("C00001", null!));
    }

    [Fact]
    public async Task Un_POST_reintentado_tras_renovar_la_sesion_crea_el_socio_una_sola_vez_con_su_cuerpo_intacto()
    {
        await using var harness = ClientHarness.AgainstMock();
        await harness.Client.Items.GetAsync("A00001");
        await harness.ExpireSessionsAsync();

        var created = await harness.Client.BusinessPartners.CreateAsync(
            new CreateBusinessPartnerRequest { CardCode = "WEB-1", CardName = "Reintentado", EmailAddress = "r@example.com" });

        created.CardName.ShouldBe("Reintentado");
        created.EmailAddress.ShouldBe("r@example.com");
        harness.Log.Sequence.Skip(2).ShouldBe(
        [
            "POST /b1s/v1/BusinessPartners -> 401",
            "POST /b1s/v1/Login -> 200",
            "POST /b1s/v1/BusinessPartners -> 201",
        ]);
        var matches = new List<BusinessPartner>();
        await foreach (var partner in harness.Client.BusinessPartners.QueryAsync(new SapQuery { Filter = SapFilter.Equal("CardCode", "WEB-1") }))
        {
            matches.Add(partner);
        }

        matches.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Creaciones_concurrentes_con_sesion_expirada_obtienen_codigos_distintos_y_un_solo_login_nuevo()
    {
        await using var harness = ClientHarness.AgainstMock();
        await harness.Client.Items.GetAsync("A00001");
        await harness.ExpireSessionsAsync();

        var created = await Task.WhenAll(Enumerable.Range(1, 10).Select(
            i => harness.Client.BusinessPartners.CreateAsync(new CreateBusinessPartnerRequest { CardName = $"Cliente {i}" })));

        created.Select(partner => partner.CardCode).Distinct().Count().ShouldBe(10);
        harness.Log.Logins.ShouldBe(2);
    }

    private static async Task<BusinessPartner?> FirstAsync(ClientHarness harness, string filter)
    {
        await foreach (var partner in harness.Client.BusinessPartners.QueryAsync(new SapQuery { Filter = filter, Top = 1 }))
        {
            return partner;
        }

        return null;
    }
}
