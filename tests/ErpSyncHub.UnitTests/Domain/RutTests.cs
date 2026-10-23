using ErpSyncHub.Domain.Customers;

namespace ErpSyncHub.UnitTests.Domain;

public class RutTests
{
    [Theory]
    [InlineData("12.345.678-5", 12345678, '5')]
    [InlineData("12345678-5", 12345678, '5')]
    [InlineData("123456785", 12345678, '5')]
    [InlineData("  12.345.678-5  ", 12345678, '5')]
    [InlineData("76.123.456-0", 76123456, '0')]
    [InlineData("77.234.567-4", 77234567, '4')]
    [InlineData("96.345.678-6", 96345678, '6')]
    [InlineData("15.678.901-1", 15678901, '1')]
    [InlineData("78.456.789-3", 78456789, '3')]
    [InlineData("76.987.654-5", 76987654, '5')]
    [InlineData("18.765.432-7", 18765432, '7')]
    [InlineData("11.111.111-1", 11111111, '1')]
    [InlineData("1-9", 1, '9')]
    [InlineData("6-K", 6, 'K')]
    [InlineData("6-k", 6, 'K')]
    public void TryParse_acepta_RUT_con_digito_verificador_correcto(string input, int number, char checkDigit)
    {
        Rut.TryParse(input, out var rut).ShouldBeTrue();

        rut.Number.ShouldBe(number);
        rut.CheckDigit.ShouldBe(checkDigit);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("-5")]
    [InlineData("5")]
    [InlineData("12.345.678-4")]
    [InlineData("12.345.678-K")]
    [InlineData("12345678")]
    [InlineData("12.345.678-KK")]
    [InlineData("1234567890")]
    [InlineData("12.34a.678-5")]
    [InlineData("0-0")]
    [InlineData("12 345 678-5")]
    [InlineData("0012345678-5")]
    public void TryParse_rechaza_RUT_invalidos(string? input)
    {
        Rut.TryParse(input, out var rut).ShouldBeFalse();

        rut.ShouldBe(default);
    }

    [Theory]
    [InlineData(12345678, '5')]
    [InlineData(76123456, '0')]
    [InlineData(6, 'K')]
    [InlineData(1, '9')]
    [InlineData(99999999, '9')]
    public void ComputeCheckDigit_aplica_modulo_11(int number, char expected)
    {
        Rut.ComputeCheckDigit(number).ShouldBe(expected);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ComputeCheckDigit_rechaza_numeros_no_positivos(int number)
    {
        Should.Throw<ArgumentOutOfRangeException>(() => Rut.ComputeCheckDigit(number));
    }

    [Fact]
    public void Un_RUT_se_expresa_en_forma_canonica_y_con_puntos()
    {
        Rut.TryParse("12345678-5", out var rut).ShouldBeTrue();

        rut.Normalized.ShouldBe("12345678-5");
        rut.Formatted.ShouldBe("12.345.678-5");
        rut.ToString().ShouldBe("12345678-5");
    }

    [Fact]
    public void Un_RUT_corto_se_formatea_sin_puntos_de_mas()
    {
        Rut.TryParse("6-K", out var rut).ShouldBeTrue();

        rut.Formatted.ShouldBe("6-K");
    }

    [Fact]
    public void Dos_RUT_escritos_distinto_son_iguales()
    {
        Rut.TryParse("12.345.678-5", out var a).ShouldBeTrue();
        Rut.TryParse("123456785", out var b).ShouldBeTrue();

        a.ShouldBe(b);
    }
}
