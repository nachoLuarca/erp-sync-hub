namespace ErpSyncHub.Domain.Common;

/// <summary>Clasificación de un error de negocio; permite decidir si tiene sentido reintentar.</summary>
public enum ErrorKind
{
    /// <summary>Los datos recibidos no son válidos.</summary>
    Validation,

    /// <summary>Un recurso necesario no existe.</summary>
    NotFound,

    /// <summary>La operación choca con el estado actual (por ejemplo, un duplicado o un job ya tomado).</summary>
    Conflict,

    /// <summary>Un sistema externo rechazó la operación de forma definitiva: reintentar no cambiará el resultado.</summary>
    Rejected,

    /// <summary>Falla pasajera de un sistema externo (red, tiempo de espera, indisponibilidad): puede reintentarse.</summary>
    Transient,

    /// <summary>Error inesperado.</summary>
    Unexpected,
}

/// <summary>Un error de negocio con un código estable, un mensaje legible y su clasificación.</summary>
/// <param name="Code">Identificador estable, por ejemplo <c>sap.rejected</c>.</param>
/// <param name="Message">Descripción para humanos.</param>
/// <param name="Kind">Clasificación del error.</param>
public sealed record ResultError(string Code, string Message, ErrorKind Kind)
{
    /// <summary>Indica si el error es pasajero y la operación puede reintentarse.</summary>
    public bool IsTransient => Kind == ErrorKind.Transient;

    public static ResultError Validation(string code, string message) => new(code, message, ErrorKind.Validation);

    public static ResultError NotFound(string code, string message) => new(code, message, ErrorKind.NotFound);

    public static ResultError Conflict(string code, string message) => new(code, message, ErrorKind.Conflict);

    public static ResultError Rejected(string code, string message) => new(code, message, ErrorKind.Rejected);

    public static ResultError Transient(string code, string message) => new(code, message, ErrorKind.Transient);

    public static ResultError Unexpected(string code, string message) => new(code, message, ErrorKind.Unexpected);
}
