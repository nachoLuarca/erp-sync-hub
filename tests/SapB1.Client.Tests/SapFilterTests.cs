namespace SapB1.Client.Tests;

public class SapFilterTests
{
    [Theory]
    [InlineData("A00001", "'A00001'")]
    [InlineData("O'Brien", "'O''Brien'")]
    [InlineData("''", "''''''")]
    [InlineData("", "''")]
    public void Quote_escapa_las_comillas_simples(string value, string expected)
    {
        SapFilter.Quote(value).ShouldBe(expected);
    }

    [Fact]
    public void Quote_rechaza_null()
    {
        Should.Throw<ArgumentNullException>(() => SapFilter.Quote(null!));
    }

    [Fact]
    public void Equal_arma_la_comparacion_con_el_valor_escapado()
    {
        SapFilter.Equal("CardName", "D'Angelo").ShouldBe("CardName eq 'D''Angelo'");
    }

    [Fact]
    public void Equal_exige_la_propiedad()
    {
        Should.Throw<ArgumentException>(() => SapFilter.Equal(" ", "x"));
    }

    [Fact]
    public void And_une_las_condiciones_entre_parentesis()
    {
        SapFilter.And("CardType eq 'cCustomer'", "Valid eq 'tYES'")
            .ShouldBe("(CardType eq 'cCustomer') and (Valid eq 'tYES')");
    }

    [Fact]
    public void And_con_una_sola_condicion_la_deja_entre_parentesis()
    {
        SapFilter.And("a eq 1").ShouldBe("(a eq 1)");
    }

    [Fact]
    public void GreaterOrEqual_formatea_la_fecha_en_ISO()
    {
        SapFilter.GreaterOrEqual("UpdateDate", new DateOnly(2026, 10, 3)).ShouldBe("UpdateDate ge '2026-10-03'");
    }

    [Fact]
    public void GreaterOrEqual_exige_la_propiedad()
    {
        Should.Throw<ArgumentException>(() => SapFilter.GreaterOrEqual("", new DateOnly(2026, 1, 1)));
    }

    [Fact]
    public void Greater_arma_la_comparacion_con_el_valor_escapado()
    {
        SapFilter.Greater("ItemCode", "A'1").ShouldBe("ItemCode gt 'A''1'");
    }
}
