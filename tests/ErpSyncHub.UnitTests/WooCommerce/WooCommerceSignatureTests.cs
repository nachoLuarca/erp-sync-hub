using System.Text;
using ErpSyncHub.Application.WooCommerce;

namespace ErpSyncHub.UnitTests.WooCommerce;

public class WooCommerceSignatureTests
{
    private const string Secret = "secreto-de-prueba-1234";
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("""{"id":5501}""");

    [Fact]
    public void Compute_coincide_con_el_vector_de_referencia_de_HMAC_SHA256()
    {
        // Vector público: clave "key" y el mensaje clásico "The quick brown fox...".
        var signature = WooCommerceSignature.Compute("key", Encoding.UTF8.GetBytes("The quick brown fox jumps over the lazy dog"));

        signature.ShouldBe("97yD9DBThCSxMpjmqm+xQ+9NWaFJRhdZl0edvC0aPNg=");
    }

    [Fact]
    public void Compute_produce_la_firma_en_Base64_del_cuerpo_con_el_secreto()
    {
        WooCommerceSignature.Compute(Secret, Body).ShouldBe("2jvVHhKn+6ne0vu+EBmCmf0mfSb5cGx1/Zn5lCkRhxY=");
    }

    [Fact]
    public void Una_firma_correcta_es_valida()
    {
        var signature = WooCommerceSignature.Compute(Secret, Body);

        WooCommerceSignature.IsValid(Secret, Body, signature).ShouldBeTrue();
    }

    [Fact]
    public void La_firma_se_calcula_sobre_los_bytes_exactos_con_tildes_incluidas()
    {
        var body = Encoding.UTF8.GetBytes("""{"billing":{"last_name":"Muñoz"}}""");
        var signature = WooCommerceSignature.Compute(Secret, body);

        WooCommerceSignature.IsValid(Secret, body, signature).ShouldBeTrue();
    }

    [Fact]
    public void Un_cuerpo_vacio_firmado_correctamente_es_valido()
    {
        var signature = WooCommerceSignature.Compute(Secret, []);

        WooCommerceSignature.IsValid(Secret, [], signature).ShouldBeTrue();
    }

    [Fact]
    public void Una_firma_con_espacios_alrededor_se_acepta()
    {
        var signature = WooCommerceSignature.Compute(Secret, Body);

        WooCommerceSignature.IsValid(Secret, Body, $"  {signature}\r\n").ShouldBeTrue();
    }

    [Fact]
    public void Un_solo_byte_distinto_en_el_cuerpo_invalida_la_firma()
    {
        var signature = WooCommerceSignature.Compute(Secret, Body);
        var tampered = Body.ToArray();
        tampered[^2] = (byte)'2';

        WooCommerceSignature.IsValid(Secret, tampered, signature).ShouldBeFalse();
    }

    [Fact]
    public void Reserializar_el_cuerpo_invalida_la_firma()
    {
        var signature = WooCommerceSignature.Compute(Secret, Body);
        var reformatted = Encoding.UTF8.GetBytes("""{ "id": 5501 }""");

        WooCommerceSignature.IsValid(Secret, reformatted, signature).ShouldBeFalse();
    }

    [Fact]
    public void Un_secreto_distinto_invalida_la_firma()
    {
        var signature = WooCommerceSignature.Compute(Secret, Body);

        WooCommerceSignature.IsValid("otro-secreto-distinto-99", Body, signature).ShouldBeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("esto-no-es-base64!!")]
    [InlineData("%%%%")]
    public void Una_firma_ausente_o_mal_formada_es_invalida(string? header)
    {
        WooCommerceSignature.IsValid(Secret, Body, header).ShouldBeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    [InlineData(31)]
    [InlineData(33)]
    [InlineData(64)]
    public void Una_firma_Base64_de_largo_distinto_a_32_bytes_es_invalida(int length)
    {
        var header = Convert.ToBase64String(new byte[length]);

        WooCommerceSignature.IsValid(Secret, Body, header).ShouldBeFalse();
    }

    [Fact]
    public void Una_firma_en_hexadecimal_no_se_acepta()
    {
        var hex = Convert.ToHexString(System.Security.Cryptography.HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Body));

        WooCommerceSignature.IsValid(Secret, Body, hex).ShouldBeFalse();
    }

    [Fact]
    public void Cambiar_mayusculas_de_la_firma_Base64_la_invalida()
    {
        var signature = WooCommerceSignature.Compute(Secret, Body);
        var swapped = new string([.. signature.Select(c => char.IsLetter(c) ? (char.IsUpper(c) ? char.ToLowerInvariant(c) : char.ToUpperInvariant(c)) : c)]);

        WooCommerceSignature.IsValid(Secret, Body, swapped).ShouldBeFalse();
    }

    [Fact]
    public void El_secreto_es_obligatorio()
    {
        Should.Throw<ArgumentException>(() => WooCommerceSignature.Compute(string.Empty, Body));
        Should.Throw<ArgumentException>(() => WooCommerceSignature.IsValid(string.Empty, Body, "x"));
    }

    [Fact]
    public void El_nombre_de_la_cabecera_es_el_que_envia_WooCommerce()
    {
        WooCommerceSignature.HeaderName.ShouldBe("X-WC-Webhook-Signature");
    }

    [Fact]
    public void Un_cuerpo_grande_se_firma_y_se_verifica()
    {
        var body = new byte[512 * 1024];
        new Random(42).NextBytes(body);
        var signature = WooCommerceSignature.Compute(Secret, body);

        WooCommerceSignature.IsValid(Secret, body, signature).ShouldBeTrue();
    }
}
