using ErpSyncHub.Domain.Common;
using ErpSyncHub.Domain.Customers;
using ErpSyncHub.Domain.Mappings;

namespace ErpSyncHub.UnitTests.Domain;

public class MappingTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ProductMapping_se_crea_con_el_SKU_y_el_ItemCode_sin_espacios()
    {
        var result = ProductMapping.Create("  TAL-650  ", " A00001 ", Now);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Sku.ShouldBe("TAL-650");
        result.Value.ItemCode.ShouldBe("A00001");
        result.Value.CreatedAt.ShouldBe(Now);
        result.Value.Id.Version.ShouldBe(7);
    }

    [Theory]
    [InlineData("", "A00001", "mapping.invalid_sku")]
    [InlineData("  ", "A00001", "mapping.invalid_sku")]
    [InlineData("TAL-650", "", "mapping.invalid_item_code")]
    [InlineData("TAL-650", " ", "mapping.invalid_item_code")]
    public void ProductMapping_rechaza_valores_vacios(string sku, string itemCode, string code)
    {
        var result = ProductMapping.Create(sku, itemCode, Now);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe(code);
        result.Error.Kind.ShouldBe(ErrorKind.Validation);
    }

    [Fact]
    public void ProductMapping_rechaza_valores_demasiado_largos()
    {
        ProductMapping.Create(new string('s', ProductMapping.MaxSkuLength + 1), "A00001", Now).IsFailure.ShouldBeTrue();
        ProductMapping.Create("TAL-650", new string('i', ProductMapping.MaxItemCodeLength + 1), Now).IsFailure.ShouldBeTrue();
        ProductMapping.Create(new string('s', ProductMapping.MaxSkuLength), new string('i', ProductMapping.MaxItemCodeLength), Now)
            .IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void CustomerMapping_se_crea_con_la_clave_y_el_CardCode()
    {
        var result = CustomerMapping.Create(CustomerKey.ForRegistered(4521), "C12345678-5", Now);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ExternalKey.ShouldBe("customer:4521");
        result.Value.CardCode.ShouldBe("C12345678-5");
        result.Value.CreatedAt.ShouldBe(Now);
    }

    [Theory]
    [InlineData("", "C1", "mapping.invalid_key")]
    [InlineData("customer:1", "", "mapping.invalid_card_code")]
    public void CustomerMapping_rechaza_valores_vacios(string key, string cardCode, string code)
    {
        CustomerMapping.Create(key, cardCode, Now).Error!.Code.ShouldBe(code);
    }

    [Fact]
    public void CustomerMapping_rechaza_un_CardCode_que_no_cabe_en_SAP()
    {
        CustomerMapping.Create("customer:1", new string('c', SapCardCodes.MaxLength + 1), Now).IsFailure.ShouldBeTrue();
        CustomerMapping.Create("customer:1", new string('c', SapCardCodes.MaxLength), Now).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void CustomerMapping_rechaza_una_clave_demasiado_larga()
    {
        CustomerMapping.Create(new string('k', CustomerMapping.MaxExternalKeyLength + 1), "C1", Now).IsFailure.ShouldBeTrue();
    }
}
