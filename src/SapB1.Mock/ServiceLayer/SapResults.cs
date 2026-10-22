namespace SapB1.Mock.ServiceLayer;

/// <summary>Respuestas HTTP con el formato del Service Layer.</summary>
internal static class SapResults
{
    public const string ContentType = "application/json;odata=minimalmetadata;charset=utf-8";

    /// <summary>Código de SAP para una sesión inexistente o expirada.</summary>
    public const int InvalidSessionCode = 301;

    public static IResult Json(object value, int statusCode = StatusCodes.Status200OK) =>
        Results.Json(value, contentType: ContentType, statusCode: statusCode);

    public static IResult Error(int statusCode, int code, string message) =>
        Json(new SapErrorResponse(new SapErrorBody(code, new SapErrorMessage("en-us", message))), statusCode);

    public static IResult InvalidSession() =>
        Error(StatusCodes.Status401Unauthorized, InvalidSessionCode, "Invalid session.");
}
