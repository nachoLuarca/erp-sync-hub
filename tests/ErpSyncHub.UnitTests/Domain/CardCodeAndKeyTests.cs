using ErpSyncHub.Domain.Customers;
using ErpSyncHub.Domain.Mappings;

namespace ErpSyncHub.UnitTests.Domain;

public class CardCodeAndKeyTests
{
    [Fact]
    public void El_CardCode_de_un_cliente_con_RUT_lleva_el_RUT_sin_puntos()
    {
        Rut.TryParse("12.345.678-5", out var rut).ShouldBeTrue();

        SapCardCodes.ForRut(rut).ShouldBe("C12345678-5");
    }

    [Fact]
    public void El_CardCode_mas_largo_posible_con_RUT_cabe_en_SAP()
    {
        Rut.TryParse("99.999.999-9", out var rut).ShouldBeTrue();

        SapCardCodes.ForRut(rut).Length.ShouldBeLessThanOrEqualTo(SapCardCodes.MaxLength);
    }

    [Fact]
    public void El_CardCode_de_un_cliente_registrado_sin_RUT_lleva_su_id()
    {
        SapCardCodes.ForStoreCustomer(4521).ShouldBe("WC4521");
    }

    [Fact]
    public void El_CardCode_de_un_cliente_registrado_cabe_en_SAP_hasta_el_mayor_id_admitido()
    {
        SapCardCodes.ForStoreCustomer(SapCardCodes.MaxStoreCustomerId).Length.ShouldBe(SapCardCodes.MaxLength);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(SapCardCodes.MaxStoreCustomerId + 1)]
    public void El_CardCode_de_un_cliente_registrado_rechaza_ids_fuera_de_rango(long id)
    {
        Should.Throw<ArgumentOutOfRangeException>(() => SapCardCodes.ForStoreCustomer(id));
    }

    [Fact]
    public void El_CardCode_de_un_invitado_es_un_hash_estable_del_correo()
    {
        SapCardCodes.ForGuest("ana@example.com").ShouldBe("WG8E43CA377012");
    }

    [Theory]
    [InlineData("Ana@Example.com")]
    [InlineData("  ana@example.com  ")]
    [InlineData("ANA@EXAMPLE.COM")]
    public void El_CardCode_de_un_invitado_no_depende_de_mayusculas_ni_espacios(string email)
    {
        SapCardCodes.ForGuest(email).ShouldBe(SapCardCodes.ForGuest("ana@example.com"));
    }

    [Fact]
    public void Correos_distintos_producen_CardCode_distintos_y_siempre_caben_en_SAP()
    {
        var a = SapCardCodes.ForGuest("ana@example.com");
        var b = SapCardCodes.ForGuest("beto@example.com");

        a.ShouldNotBe(b);
        a.Length.ShouldBe(14);
        b.Length.ShouldBeLessThanOrEqualTo(SapCardCodes.MaxLength);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void El_CardCode_de_un_invitado_exige_correo(string? email)
    {
        Should.Throw<ArgumentException>(() => SapCardCodes.ForGuest(email!));
    }

    [Fact]
    public void La_clave_de_un_cliente_registrado_lleva_su_id()
    {
        CustomerKey.ForRegistered(4521).ShouldBe("customer:4521");
    }

    [Fact]
    public void La_clave_de_un_invitado_normaliza_el_correo()
    {
        CustomerKey.ForGuest("  Ana@Example.COM ").ShouldBe("email:ana@example.com");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void La_clave_de_un_cliente_registrado_rechaza_ids_no_positivos(long id)
    {
        Should.Throw<ArgumentOutOfRangeException>(() => CustomerKey.ForRegistered(id));
    }
}
