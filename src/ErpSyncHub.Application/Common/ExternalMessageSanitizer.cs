namespace ErpSyncHub.Application.Common;

/// <summary>
/// Cómo se acotan los mensajes que repiten los sistemas externos (sección <c>Messages</c>, variable
/// <c>Messages__ExternalMaxLength</c>; en Compose, <c>ERPSYNC_EXTERNAL_MESSAGE_MAX_LENGTH</c>).
/// </summary>
public sealed class ExternalMessageOptions
{
    public const string SectionName = "Messages";

    /// <summary>Largo mínimo admitido: menos que esto no deja ver ni el motivo de un error.</summary>
    public const int MinLength = 50;

    /// <summary>Largo máximo admitido; cabe en el campo del último error del job.</summary>
    public const int MaxLength = 2000;

    /// <summary>
    /// Largo máximo de un mensaje de SAP o de la tienda antes de guardarlo y registrarlo. Un sistema externo puede responder con
    /// una página entera; acotarlo evita llenar el job y los logs, y deja menos espacio donde esconder datos personales. Por
    /// defecto, 300.
    /// </summary>
    public int ExternalMaxLength { get; set; } = 300;

    /// <summary>Devuelve los motivos por los que la configuración no es válida; vacío si lo es.</summary>
    public IReadOnlyList<string> Validate() =>
        ExternalMaxLength is < MinLength or > MaxLength
            ? [$"Messages:ExternalMaxLength debe estar entre {MinLength} y {MaxLength} (variable de entorno Messages__ExternalMaxLength, en Compose ERPSYNC_EXTERNAL_MESSAGE_MAX_LENGTH)."]
            : [];
}

/// <summary>
/// Deja un mensaje de un sistema externo (SAP, la tienda, una excepción) listo para guardarlo en un job y escribirlo en el log:
/// sin datos personales y con un largo acotado. Primero oculta y después recorta, para que el corte no deje a medias un dato
/// que ya no se podría reconocer.
/// </summary>
public sealed class ExternalMessageSanitizer(ExternalMessageOptions options)
{
    private const string Ellipsis = "…";

    /// <summary>El mensaje sin datos personales y recortado al largo máximo; <see langword="null"/> queda vacío.</summary>
    public string Sanitize(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return string.Empty;
        }

        return Truncate(PersonalDataRedactor.RedactRequired(message));
    }

    private string Truncate(string text)
    {
        var max = options.ExternalMaxLength;
        if (text.Length <= max)
        {
            return text;
        }

        var keep = max - Ellipsis.Length;

        // No se corta un par sustituto por la mitad.
        if (keep > 0 && char.IsHighSurrogate(text[keep - 1]))
        {
            keep--;
        }

        return text[..keep] + Ellipsis;
    }
}
