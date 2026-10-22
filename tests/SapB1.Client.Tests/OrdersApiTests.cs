using System.Net;
using SapB1.Client.Models;
using SapB1.Client.Tests.Support;

namespace SapB1.Client.Tests;

public class OrdersApiTests
{
    [Fact]
    public async Task CreateAsync_devuelve_la_orden_con_los_datos_que_asigna_SAP()
    {
        await using var harness = ClientHarness.AgainstMock();

        var order = await harness.Client.Orders.CreateAsync(new CreateOrderRequest
        {
            CardCode = "C00001",
            NumAtCard = "WOO-1001",
            Comments = "Pedido web",
            DocumentLines =
            [
                new CreateOrderLine { ItemCode = "A00001", Quantity = 2 },
                new CreateOrderLine { ItemCode = "A00010", Quantity = 3, UnitPrice = 2500 },
            ],
        });

        order.DocEntry.ShouldBe(1);
        order.DocNum.ShouldBe(1001);
        order.CardCode.ShouldBe("C00001");
        order.CardName.ShouldBe("Constructora Los Andes SpA");
        order.NumAtCard.ShouldBe("WOO-1001");
        order.Comments.ShouldBe("Pedido web");
        order.DocCurrency.ShouldBe("CLP");
        order.DocumentStatus.ShouldBe(DocumentStatuses.Open);
        order.Cancelled.ShouldBe(YesNo.No);
        order.DocTotal.ShouldBe(87480m);
        order.DocDate.ShouldNotBeNull();
        order.DocDueDate.ShouldBe(order.DocDate);

        order.DocumentLines.Count.ShouldBe(2);
        order.DocumentLines[0].ShouldBe(new DocumentLine
        {
            LineNum = 0,
            ItemCode = "A00001",
            ItemDescription = "Taladro percutor 650 W",
            Quantity = 2m,
            UnitPrice = 39990m,
            LineTotal = 79980m,
            WarehouseCode = "01",
            LineStatus = DocumentStatuses.Open,
        });
        order.DocumentLines[1].UnitPrice.ShouldBe(2500m);
        order.DocumentLines[1].LineTotal.ShouldBe(7500m);
    }

    [Fact]
    public async Task CreateAsync_respeta_las_fechas_y_las_cantidades_decimales()
    {
        await using var harness = ClientHarness.AgainstMock();

        var order = await harness.Client.Orders.CreateAsync(new CreateOrderRequest
        {
            CardCode = "C00001",
            DocDate = new DateOnly(2026, 2, 1),
            DocDueDate = new DateOnly(2026, 2, 10),
            DocumentLines = [new CreateOrderLine { ItemCode = "A00015", Quantity = 2.5m }],
        });

        order.DocDate.ShouldBe(new DateOnly(2026, 2, 1));
        order.DocDueDate.ShouldBe(new DateOnly(2026, 2, 10));
        order.DocumentLines[0].Quantity.ShouldBe(2.5m);
        order.DocumentLines[0].LineTotal.ShouldBe(4975m);
    }

    [Fact]
    public async Task GetAsync_devuelve_la_orden_por_DocEntry()
    {
        await using var harness = ClientHarness.AgainstMock();
        var created = await harness.Client.Orders.CreateAsync(NewOrder("WOO-1"));

        var order = await harness.Client.Orders.GetAsync(created.DocEntry);

        order.ShouldNotBeNull();
        order.DocEntry.ShouldBe(created.DocEntry);
        order.NumAtCard.ShouldBe("WOO-1");
        order.DocumentLines.ShouldHaveSingleItem().ItemCode.ShouldBe("A00001");
    }

    [Fact]
    public async Task GetAsync_de_una_orden_inexistente_devuelve_null()
    {
        await using var harness = ClientHarness.AgainstMock();

        (await harness.Client.Orders.GetAsync(999)).ShouldBeNull();
    }

    [Fact]
    public async Task QueryAsync_por_NumAtCard_detecta_pedidos_ya_enviados()
    {
        await using var harness = ClientHarness.AgainstMock();
        await harness.Client.Orders.CreateAsync(NewOrder("WOO-1"));
        await harness.Client.Orders.CreateAsync(NewOrder("WOO-2"));

        var found = await ListAsync(harness, new SapQuery { Filter = SapFilter.Equal("NumAtCard", "WOO-2") });
        var missing = await ListAsync(harness, new SapQuery { Filter = SapFilter.Equal("NumAtCard", "WOO-3") });

        found.ShouldHaveSingleItem().DocEntry.ShouldBe(2);
        missing.ShouldBeEmpty();
    }

    [Fact]
    public async Task QueryAsync_permite_obtener_la_ultima_orden()
    {
        await using var harness = ClientHarness.AgainstMock();
        await harness.Client.Orders.CreateAsync(NewOrder("WOO-1"));
        await harness.Client.Orders.CreateAsync(NewOrder("WOO-2"));

        var latest = await ListAsync(harness, new SapQuery { OrderBy = "DocEntry desc", Top = 1, Select = ["DocEntry", "NumAtCard"] });

        var order = latest.ShouldHaveSingleItem();
        order.DocEntry.ShouldBe(2);
        order.NumAtCard.ShouldBe("WOO-2");
        order.CardCode.ShouldBeEmpty();
    }

    [Fact]
    public async Task CreateAsync_compromete_stock_visible_en_Items()
    {
        await using var harness = ClientHarness.AgainstMock();

        await harness.Client.Orders.CreateAsync(NewOrder("WOO-1", quantity: 4));

        var item = await harness.Client.Items.GetAsync("A00001");
        var warehouse = item.ShouldNotBeNull().ItemWarehouseInfoCollection.ShouldHaveSingleItem();
        warehouse.Committed.ShouldBe(4m);
        warehouse.InStock.ShouldBe(45m);
    }

    [Theory]
    [InlineData("NO-EXISTE", "A00001", 1, "Business partner 'NO-EXISTE' does not exist")]
    [InlineData("P00001", "A00001", 1, "vendor")]
    [InlineData("C00001", "ZZZ", 1, "Item 'ZZZ' does not exist (line 0)")]
    [InlineData("C00001", "A00001", 0, "Quantity must be greater than zero (line 0)")]
    [InlineData("C00001", "A00001", -2, "Quantity must be greater than zero")]
    public async Task CreateAsync_invalida_lanza_SapB1ApiException_con_el_motivo(
        string cardCode, string itemCode, int quantity, string expectedMessage)
    {
        await using var harness = ClientHarness.AgainstMock();

        var exception = await Should.ThrowAsync<SapB1ApiException>(() => harness.Client.Orders.CreateAsync(new CreateOrderRequest
        {
            CardCode = cardCode,
            DocumentLines = [new CreateOrderLine { ItemCode = itemCode, Quantity = quantity }],
        }));

        exception.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        exception.SapErrorCode.ShouldBe(-10);
        exception.SapErrorMessage.ShouldNotBeNull().ShouldContain(expectedMessage);
    }

    [Fact]
    public async Task CreateAsync_sin_lineas_lanza_SapB1ApiException()
    {
        await using var harness = ClientHarness.AgainstMock();

        var exception = await Should.ThrowAsync<SapB1ApiException>(
            () => harness.Client.Orders.CreateAsync(new CreateOrderRequest { CardCode = "C00001", DocumentLines = [] }));

        exception.SapErrorMessage.ShouldNotBeNull().ShouldContain("DocumentLines");
    }

    [Fact]
    public async Task CreateAsync_con_NumAtCard_demasiado_largo_lanza_SapB1ApiException()
    {
        await using var harness = ClientHarness.AgainstMock();

        var exception = await Should.ThrowAsync<SapB1ApiException>(
            () => harness.Client.Orders.CreateAsync(NewOrder(new string('x', 101))));

        exception.SapErrorMessage.ShouldNotBeNull().ShouldContain("NumAtCard");
    }

    [Fact]
    public async Task CreateAsync_con_una_linea_invalida_no_crea_la_orden_ni_compromete_stock()
    {
        await using var harness = ClientHarness.AgainstMock();

        await Should.ThrowAsync<SapB1ApiException>(() => harness.Client.Orders.CreateAsync(new CreateOrderRequest
        {
            CardCode = "C00001",
            DocumentLines =
            [
                new CreateOrderLine { ItemCode = "A00001", Quantity = 2 },
                new CreateOrderLine { ItemCode = "NO-EXISTE", Quantity = 1 },
            ],
        }));

        (await ListAsync(harness, null)).ShouldBeEmpty();
        var item = await harness.Client.Items.GetAsync("A00001");
        item.ShouldNotBeNull().ItemWarehouseInfoCollection.ShouldHaveSingleItem().Committed.ShouldBe(0m);
    }

    [Fact]
    public async Task CreateAsync_rechaza_una_solicitud_nula()
    {
        await using var harness = ClientHarness.AgainstMock();

        await Should.ThrowAsync<ArgumentNullException>(() => harness.Client.Orders.CreateAsync(null!));
    }

    [Fact]
    public async Task Un_POST_reintentado_tras_renovar_la_sesion_crea_una_sola_orden_y_compromete_el_stock_una_vez()
    {
        await using var harness = ClientHarness.AgainstMock();
        await harness.Client.Items.GetAsync("A00001");
        await harness.ExpireSessionsAsync();

        var order = await harness.Client.Orders.CreateAsync(NewOrder("WOO-1", quantity: 3));

        order.DocEntry.ShouldBe(1);
        harness.Log.Sequence.Skip(2).ShouldBe(
        [
            "POST /b1s/v1/Orders -> 401",
            "POST /b1s/v1/Login -> 200",
            "POST /b1s/v1/Orders -> 201",
        ]);
        (await ListAsync(harness, null)).ShouldHaveSingleItem();
        var item = await harness.Client.Items.GetAsync("A00001");
        item.ShouldNotBeNull().ItemWarehouseInfoCollection.ShouldHaveSingleItem().Committed.ShouldBe(3m);
    }

    [Fact]
    public async Task Ordenes_concurrentes_reciben_DocEntry_y_DocNum_distintos()
    {
        await using var harness = ClientHarness.AgainstMock();

        var orders = await Task.WhenAll(Enumerable.Range(1, 10).Select(i => harness.Client.Orders.CreateAsync(NewOrder($"WOO-{i}"))));

        orders.Select(o => o.DocEntry).Order().ShouldBe(Enumerable.Range(1, 10));
        orders.Select(o => o.DocNum).Distinct().Count().ShouldBe(10);
        harness.Log.Logins.ShouldBe(1);
    }

    [Fact]
    public async Task Flujo_completo_de_un_pedido_web_es_idempotente()
    {
        await using var harness = ClientHarness.AgainstMock();
        var client = harness.Client;
        const string rut = "18.765.432-1";
        const string wooOrderId = "WOO-5501";

        // 1. Buscar el cliente por RUT y crearlo si no existe.
        BusinessPartner? customer = null;
        await foreach (var found in client.BusinessPartners.QueryAsync(new SapQuery { Filter = SapFilter.Equal("FederalTaxID", rut), Top = 1 }))
        {
            customer = found;
        }

        customer.ShouldBeNull();
        customer = await client.BusinessPartners.CreateAsync(new CreateBusinessPartnerRequest
        {
            CardName = "Valentina Muñoz",
            FederalTaxID = rut,
            EmailAddress = "valentina@example.com",
        });

        // 2. Crear la orden solo si el pedido no fue enviado antes.
        async Task<Order> EnsureOrderAsync()
        {
            await foreach (var existing in client.Orders.QueryAsync(new SapQuery { Filter = SapFilter.Equal("NumAtCard", wooOrderId), Top = 1 }))
            {
                return existing;
            }

            return await client.Orders.CreateAsync(new CreateOrderRequest
            {
                CardCode = customer.CardCode,
                NumAtCard = wooOrderId,
                DocumentLines = [new CreateOrderLine { ItemCode = "A00002", Quantity = 1 }],
            });
        }

        var first = await EnsureOrderAsync();
        var retried = await EnsureOrderAsync();

        first.CardCode.ShouldBe(customer.CardCode);
        retried.DocEntry.ShouldBe(first.DocEntry);
        (await ListAsync(harness, null)).ShouldHaveSingleItem();
    }

    private static CreateOrderRequest NewOrder(string numAtCard, decimal quantity = 1) => new()
    {
        CardCode = "C00001",
        NumAtCard = numAtCard,
        DocumentLines = [new CreateOrderLine { ItemCode = "A00001", Quantity = quantity }],
    };

    private static async Task<List<Order>> ListAsync(ClientHarness harness, SapQuery? query)
    {
        var orders = new List<Order>();
        await foreach (var order in harness.Client.Orders.QueryAsync(query))
        {
            orders.Add(order);
        }

        return orders;
    }
}
