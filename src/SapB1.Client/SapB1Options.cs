namespace SapB1.Client;

/// <summary>Configuración del cliente del Service Layer (sección <c>SAP</c> por defecto).</summary>
public sealed class SapB1Options
{
    /// <summary>Nombre de la sección de configuración por defecto.</summary>
    public const string SectionName = "SAP";

    /// <summary>
    /// URL base del servidor, sin la ruta del servicio: por ejemplo <c>https://sap.empresa.cl:50000</c>.
    /// El cliente agrega <c>/b1s/v1/</c>.
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Base de datos de la sociedad (<c>CompanyDB</c>).</summary>
    public string CompanyDb { get; set; } = string.Empty;

    public string UserName { get; set; } = string.Empty;

    /// <summary>Contraseña del usuario de SAP. Debe venir de una variable de entorno o un gestor de secretos.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>Tiempo máximo de cada petición HTTP. Por defecto, 30 segundos.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Filas por página que se piden con <c>Prefer: odata.maxpagesize</c> al recorrer colecciones.
    /// Por defecto, 100 (el Service Layer devuelve 20 si no se indica).
    /// </summary>
    public int PageSize { get; set; } = 100;

    /// <summary>Raíz del Service Layer v1 a partir de <see cref="BaseUrl"/>.</summary>
    internal Uri ServiceLayerUri => new($"{BaseUrl.TrimEnd('/')}/b1s/v1/", UriKind.Absolute);
}
