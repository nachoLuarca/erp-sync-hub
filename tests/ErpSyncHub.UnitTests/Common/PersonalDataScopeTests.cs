using ErpSyncHub.Application.Common;

namespace ErpSyncHub.UnitTests.Common;

/// <summary>Los datos del pedido en proceso se ocultan en los mensajes externos, escritos como se escriban.</summary>
public class PersonalDataScopeTests
{
    private static IDisposable Begin(params (PersonalDataKind Kind, string Value)[] values) =>
        PersonalDataScope.Begin(values.Select(value => new PersonalValue(value.Kind, value.Value)));

    [Theory]
    [InlineData("Cliente 'Valentina Muñoz' no existe", "Cliente '[nombre]' no existe")]
    [InlineData("Cliente VALENTINA MUÑOZ no existe", "Cliente [nombre] no existe")]
    [InlineData("Cliente valentina munoz no existe", "Cliente [nombre] no existe")]
    [InlineData("Cliente Valentina    Muñoz no existe", "Cliente [nombre] no existe")]
    [InlineData("Cliente Valentina\tMuñoz no existe", "Cliente [nombre] no existe")]
    [InlineData("Nombre Valentina Muñoz, Valentina Muñoz", "Nombre [nombre], [nombre]")]
    public void Oculta_el_nombre_sin_importar_mayusculas_tildes_ni_espacios(string text, string expected)
    {
        using var _ = Begin((PersonalDataKind.Name, "Valentina Muñoz"));

        PersonalDataScope.Redact(text).ShouldBe(expected);
    }

    [Fact]
    public void Un_valor_con_tildes_y_mayusculas_en_el_pedido_se_encuentra_escrito_sin_ellas()
    {
        using var _ = Begin((PersonalDataKind.Name, "JOSÉ  PÉREZ"));

        PersonalDataScope.Redact("el cliente jose perez ya existe").ShouldBe("el cliente [nombre] ya existe");
    }

    [Fact]
    public void Conserva_las_tildes_del_resto_del_texto()
    {
        using var _ = Begin((PersonalDataKind.Name, "Valentina Muñoz"));

        PersonalDataScope.Redact("Código inválido para Valentina Muñoz en dirección").ShouldBe("Código inválido para [nombre] en dirección");
    }

    [Theory]
    [InlineData("Dirección 'Av. Providencia 1234' inválida", "Dirección '[dirección]' inválida")]
    [InlineData("Dirección AV. PROVIDENCIA 1234 inválida", "Dirección [dirección] inválida")]
    [InlineData("Calle av providencia 1234, depto 56", "Calle av providencia 1234, depto 56")] // sin el punto no es el mismo valor
    public void Oculta_la_direccion_exacta(string text, string expected)
    {
        using var _ = Begin((PersonalDataKind.Address, "Av. Providencia 1234"));

        PersonalDataScope.Redact(text).ShouldBe(expected);
    }

    [Theory]
    [InlineData("Teléfono +56912345678 inválido", "Teléfono [teléfono] inválido")]
    [InlineData("Teléfono +56 9 1234 5678 inválido", "Teléfono [teléfono] inválido")]
    [InlineData("Teléfono 9 1234 5678 inválido", "Teléfono [teléfono] inválido")]
    [InlineData("Teléfono (+56) 9-1234-5678 inválido", "Teléfono ([teléfono] inválido")] // el paréntesis que abre no es parte del número
    [InlineData("Teléfono 912345678 inválido", "Teléfono [teléfono] inválido")]
    [InlineData("Teléfono 911111111 inválido", "Teléfono 911111111 inválido")]
    public void Oculta_el_telefono_por_sus_digitos_aunque_se_escriba_distinto(string text, string expected)
    {
        using var _ = Begin((PersonalDataKind.Phone, "+56 9 1234 5678"));

        PersonalDataScope.Redact(text).ShouldBe(expected);
    }

    [Theory]
    [InlineData("RUT 187654327 inválido", "RUT [RUT] inválido")]
    [InlineData("RUT 18765432-7 inválido", "RUT [RUT] inválido")]
    [InlineData("RUT 18.765.432-7 inválido", "RUT [RUT] inválido")]
    public void Oculta_el_RUT_del_pedido_en_cualquiera_de_sus_formas(string text, string expected)
    {
        using var _ = Begin(
            (PersonalDataKind.TaxId, "18.765.432-7"),
            (PersonalDataKind.TaxId, "18765432-7"),
            (PersonalDataKind.TaxId, "187654327"));

        PersonalDataScope.Redact(text).ShouldBe(expected);
    }

    [Fact]
    public void Oculta_el_correo_sin_importar_las_mayusculas()
    {
        using var _ = Begin((PersonalDataKind.Email, "valentina@example.com"));

        PersonalDataScope.Redact("Cliente VALENTINA@Example.COM duplicado").ShouldBe("Cliente [correo] duplicado");
    }

    [Fact]
    public void Los_valores_cortos_no_se_buscan_porque_destruirian_los_mensajes()
    {
        using var _ = Begin((PersonalDataKind.Name, "Ana"), (PersonalDataKind.Address, "S/N"));

        PersonalDataScope.Redact("Ana: dirección S/N inválida").ShouldBe("Ana: dirección S/N inválida");
    }

    [Fact]
    public void No_oculta_un_valor_que_es_parte_de_otra_palabra()
    {
        using var _ = Begin((PersonalDataKind.Name, "Pedro Soto"));

        PersonalDataScope.Redact("Pedro Sotomayor").ShouldBe("Pedro Sotomayor");
        PersonalDataScope.Redact("(Pedro Soto)").ShouldBe("([nombre])");
    }

    [Fact]
    public void Un_valor_largo_prevalece_sobre_uno_que_contiene()
    {
        using var _ = Begin((PersonalDataKind.Address, "Los Leones 123"), (PersonalDataKind.Address, "Av. Los Leones 123, Providencia"));

        PersonalDataScope.Redact("Dirección Av. Los Leones 123, Providencia inválida").ShouldBe("Dirección [dirección] inválida");
    }

    [Fact]
    public void Sin_ambito_el_texto_queda_igual()
    {
        PersonalDataScope.Redact("Cliente Valentina Muñoz").ShouldBe("Cliente Valentina Muñoz");
    }

    [Fact]
    public void Al_cerrar_el_ambito_se_restaura_el_anterior()
    {
        using var outer = Begin((PersonalDataKind.Name, "Valentina Muñoz"));

        using (Begin((PersonalDataKind.Name, "Luis Fuentes")))
        {
            PersonalDataScope.Redact("Luis Fuentes y Valentina Muñoz").ShouldBe("[nombre] y Valentina Muñoz");
        }

        PersonalDataScope.Redact("Luis Fuentes y Valentina Muñoz").ShouldBe("Luis Fuentes y [nombre]");
    }

    [Fact]
    public async Task Cada_flujo_asincrono_ve_solo_su_pedido_aunque_corran_a_la_vez()
    {
        async Task<string?> RunAsync(string name, string text)
        {
            using var _ = Begin((PersonalDataKind.Name, name));
            await Task.Delay(30);
            return PersonalDataScope.Redact(text);
        }

        var results = await Task.WhenAll(
            RunAsync("Valentina Muñoz", "Valentina Muñoz y Luis Fuentes"),
            RunAsync("Luis Fuentes", "Valentina Muñoz y Luis Fuentes"));

        results[0].ShouldBe("[nombre] y Luis Fuentes");
        results[1].ShouldBe("Valentina Muñoz y [nombre]");
        PersonalDataScope.Redact("Valentina Muñoz").ShouldBe("Valentina Muñoz");
    }

    [Fact]
    public void El_recortador_de_mensajes_usa_el_ambito_ademas_de_los_patrones()
    {
        using var _ = Begin((PersonalDataKind.Name, "Valentina Muñoz"));
        var sanitizer = new ExternalMessageSanitizer(new ExternalMessageOptions());

        sanitizer.Sanitize("Cliente VALENTINA MUNOZ con RUT 18.765.432-7").ShouldBe("Cliente [nombre] con RUT [RUT]");
    }
}
