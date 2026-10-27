using ErpSyncHub.Application.Common;

namespace ErpSyncHub.UnitTests.Common;

public class PersonalDataRedactorTests
{
    [Theory]
    [InlineData("Business partner 'C18765432-7' does not exist", "Business partner 'C[RUT]' does not exist")]
    [InlineData("El RUT 18.765.432-7 no existe", "El RUT [RUT] no existe")]
    [InlineData("El RUT 18765432-7 no existe", "El RUT [RUT] no existe")]
    [InlineData("RUT 1.234.567-K inválido", "RUT [RUT] inválido")]
    [InlineData("RUT 1234567-k inválido", "RUT [RUT] inválido")]
    [InlineData("Duplicado FederalTaxID '12.345.678-5'", "Duplicado FederalTaxID '[RUT]'")]
    [InlineData("Cliente valentina@example.com ya existe", "Cliente [correo] ya existe")]
    [InlineData("Correos ana.perez+tienda@mi-empresa.co.cl y luis@example.com", "Correos [correo] y [correo]")]
    [InlineData("RUT 18.765.432-7 con correo valentina@example.com", "RUT [RUT] con correo [correo]")]
    public void Oculta_RUT_y_correos(string input, string expected)
    {
        PersonalDataRedactor.Redact(input).ShouldBe(expected);
    }

    [Theory]
    [InlineData("SAP rechazó la operación (HTTP 400, código -10): CardName es obligatorio")]
    [InlineData("El artículo A00001 no existe")]
    [InlineData("Orden 1001 creada en SAP para el pedido 5501")]
    [InlineData("Total 100013.55 CLP")]
    [InlineData("Pedido 150348 del 2026-10-27")]
    [InlineData("ID 123-4 y 12345-6 no son RUT")]
    [InlineData("Teléfono +56912345678 no se reconoce por su forma")]
    public void No_toca_lo_que_no_es_un_RUT_ni_un_correo(string input)
    {
        PersonalDataRedactor.Redact(input).ShouldBe(input);
    }

    [Fact]
    public void Un_numero_largo_que_contiene_digitos_de_un_RUT_no_se_confunde()
    {
        // 9 dígitos seguidos de guion y dígito: no es un RUT (máximo 8 dígitos antes del guion).
        PersonalDataRedactor.Redact("Referencia 123456789-5").ShouldBe("Referencia 123456789-5");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Nulo_y_vacio_se_devuelven_tal_cual(string? input)
    {
        PersonalDataRedactor.Redact(input).ShouldBe(input);
    }

    [Fact]
    public void RedactRequired_nunca_devuelve_nulo()
    {
        PersonalDataRedactor.RedactRequired("18.765.432-7").ShouldBe("[RUT]");
        PersonalDataRedactor.RedactRequired(string.Empty).ShouldBe(string.Empty);
    }

    [Fact]
    public void Es_idempotente()
    {
        var once = PersonalDataRedactor.Redact("Business partner 'C18765432-7' / valentina@example.com");

        PersonalDataRedactor.Redact(once).ShouldBe(once);
    }

    [Fact]
    public void Un_texto_muy_largo_se_procesa_sin_demora_excesiva()
    {
        var text = string.Concat(Enumerable.Repeat("dato-1234567-8 sin rut 12.345 ", 20_000));

        var started = System.Diagnostics.Stopwatch.StartNew();
        PersonalDataRedactor.Redact(text);

        started.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2));
    }
}
