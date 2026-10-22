namespace SapB1.Mock.ServiceLayer;

/// <summary>Respuestas HTTP con el formato del Service Layer.</summary>
internal static class SapResults
{
    public const string ContentType = "application/json;odata=minimalmetadata;charset=utf-8";

    public static IResult Json(object value, int statusCode = StatusCodes.Status200OK) =>
        Results.Json(value, contentType: ContentType, statusCode: statusCode);

    public static IResult Error(int statusCode, int code, string message) =>
        Json(new SapErrorResponse(new SapErrorBody(code, new SapErrorMessage("en-us", message))), statusCode);

    public static IResult InvalidSession() =>
        Error(StatusCodes.Status401Unauthorized, SapErrorCodes.InvalidSession, "Invalid session.");

    public static IResult NotFound() =>
        Error(StatusCodes.Status404NotFound, SapErrorCodes.NotFound, "No matching records found (ODBC -2028)");
}
