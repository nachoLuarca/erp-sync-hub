using ErpSyncHub.Application.Common;

namespace ErpSyncHub.UnitTests.Common;

/// <summary>Los mensajes de los sistemas externos se guardan sin datos personales y con un largo acotado.</summary>
public class ExternalMessageSanitizerTests
{
    private static ExternalMessageSanitizer Sanitizer(int maxLength = 100) =>
        new(new ExternalMessageOptions { ExternalMaxLength = maxLength });

    [Fact]
    public void Un_mensaje_corto_queda_igual()
    {
        Sanitizer().Sanitize("Item 'X' does not exist").ShouldBe("Item 'X' does not exist");
    }

    [Fact]
    public void Un_mensaje_largo_se_recorta_al_maximo_con_puntos_suspensivos()
    {
        var result = Sanitizer(100).Sanitize(new string('a', 5000));

        result.Length.ShouldBe(100);
        result.ShouldEndWith("…");
    }

    [Fact]
    public void Un_mensaje_del_largo_exacto_no_se_recorta()
    {
        var exact = new string('a', 100);

        Sanitizer(100).Sanitize(exact).ShouldBe(exact);
    }

    [Fact]
    public void Primero_oculta_y_despues_recorta_asi_el_corte_no_deja_un_pedazo_de_RUT()
    {
        // El RUT empieza antes del corte y termina después: si se recortara primero, quedaría "18.765" a la vista.
        var message = new string('a', 90) + " 18.765.432-7 y mas texto";

        var result = Sanitizer(100).Sanitize(message);

        result.ShouldNotContain("18.");
        result.ShouldNotContain("765");
        result.Length.ShouldBeLessThanOrEqualTo(100);
    }

    [Fact]
    public void No_corta_un_par_sustituto_por_la_mitad()
    {
        var message = new string('a', 98) + "😀😀😀";

        var result = Sanitizer(100).Sanitize(message);

        result.Length.ShouldBeLessThanOrEqualTo(100);
        result.ShouldNotContain("\uD83D" + "…"); // un sustituto alto suelto antes de los puntos suspensivos
        result.EnumerateRunes().ShouldAllBe(rune => rune.Value != 0xFFFD);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Nulo_y_vacio_dan_un_texto_vacio(string? message)
    {
        Sanitizer().Sanitize(message).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(49, false)]
    [InlineData(50, true)]
    [InlineData(300, true)]
    [InlineData(2000, true)]
    [InlineData(2001, false)]
    public void El_largo_maximo_configurable_tiene_limites(int length, bool valid)
    {
        var errors = new ExternalMessageOptions { ExternalMaxLength = length }.Validate();

        (errors.Count == 0).ShouldBe(valid);
        if (!valid)
        {
            errors.Single().ShouldContain("Messages__ExternalMaxLength");
            errors.Single().ShouldContain("ERPSYNC_EXTERNAL_MESSAGE_MAX_LENGTH");
        }
    }

    [Fact]
    public void El_largo_por_defecto_es_300()
    {
        new ExternalMessageOptions().ExternalMaxLength.ShouldBe(300);
    }
}
