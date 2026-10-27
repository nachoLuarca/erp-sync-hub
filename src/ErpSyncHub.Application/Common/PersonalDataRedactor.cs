using System.Text.RegularExpressions;

namespace ErpSyncHub.Application.Common;

/// <summary>
/// Oculta los datos personales que un sistema externo repite en sus mensajes de error. SAP y WooCommerce escriben sus errores
/// como les parece (por ejemplo, <c>Business partner 'C18765432-7' does not exist</c>) y el hub los guarda en el job y los
/// registra: sin esto, el RUT del cliente, que además forma parte del <c>CardCode</c> de un cliente nuevo, terminaría en los logs.
/// </summary>
/// <remarks>
/// Es una defensa por patrones, no una garantía: reconoce RUT chilenos (con o sin puntos, también dentro de un código de cliente) y
/// correos. Un nombre o una dirección escritos en un mensaje de error de un sistema externo no se pueden reconocer por su forma;
/// los mensajes propios del hub nunca los incluyen.
/// </remarks>
public static partial class PersonalDataRedactor
{
    /// <summary>Lo que reemplaza a un RUT.</summary>
    public const string RutPlaceholder = "[RUT]";

    /// <summary>Lo que reemplaza a un correo.</summary>
    public const string EmailPlaceholder = "[correo]";

    /// <summary>Devuelve el texto sin RUT ni correos; <see langword="null"/> y vacío se devuelven tal cual.</summary>
    public static string? Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var withoutEmails = Email().Replace(text, EmailPlaceholder);
        var withoutDottedRut = DottedRut().Replace(withoutEmails, RutPlaceholder);
        return PlainRut().Replace(withoutDottedRut, RutPlaceholder);
    }

    /// <summary>Como <see cref="Redact"/>, para texto que nunca es <see langword="null"/>.</summary>
    public static string RedactRequired(string text) => Redact(text) ?? string.Empty;

    // 12.345.678-5 y 1.234.567-K.
    [GeneratedRegex(@"(?<!\d)\d{1,2}(?:\.\d{3}){2}-[\dkK](?![\w])", RegexOptions.CultureInvariant)]
    private static partial Regex DottedRut();

    // 12345678-5, 1234567-K y el RUT dentro de un código de cliente como C12345678-5 (precedido por una letra, no por un dígito).
    [GeneratedRegex(@"(?<!\d)\d{7,8}-[\dkK](?![\w])", RegexOptions.CultureInvariant)]
    private static partial Regex PlainRut();

    [GeneratedRegex(@"[\w.+\-]+@[\w\-]+(?:\.[\w\-]+)+", RegexOptions.CultureInvariant)]
    private static partial Regex Email();
}
