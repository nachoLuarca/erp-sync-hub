using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using SapB1.Mock.Configuration;
using SapB1.Mock.Sessions;

namespace SapB1.Mock.ServiceLayer;

internal static class SessionEndpoints
{
    private const string RouteIdCookie = "ROUTEID";
    private const string RouteIdValue = ".node1";

    public static void MapLogin(this RouteGroupBuilder serviceLayer)
    {
        serviceLayer.MapPost("/Login", LoginAsync);
    }

    public static void MapLogout(this RouteGroupBuilder securedServiceLayer)
    {
        securedServiceLayer.MapPost("/Logout", Logout);
    }

    private static async Task<IResult> LoginAsync(
        HttpContext http,
        ISessionStore sessions,
        IOptions<MockOptions> options,
        CancellationToken cancellationToken)
    {
        LoginRequest? request;
        try
        {
            request = await http.Request.ReadFromJsonAsync<LoginRequest>(cancellationToken);
        }
        catch (JsonException)
        {
            request = null;
        }

        if (request is null)
        {
            return SapResults.Error(StatusCodes.Status400BadRequest, SapErrorCodes.InvalidRequest, "Invalid JSON format.");
        }

        var expected = options.Value;
        var valid = request.CompanyDB == expected.CompanyDb
            && request.UserName == expected.UserName
            && request.Password == expected.Password;

        if (!valid)
        {
            return SapResults.Error(StatusCodes.Status401Unauthorized, SapErrorCodes.InvalidCredentials, "Fail to get DB credentials from B1SC.");
        }

        var sessionId = sessions.Create();

        http.Response.Cookies.Append(
            SessionRequiredFilter.CookieName,
            sessionId,
            new CookieOptions { Path = "/b1s/v1", HttpOnly = true });
        http.Response.Cookies.Append(RouteIdCookie, RouteIdValue, new CookieOptions { Path = "/b1s" });

        var metadata = $"{http.Request.Scheme}://{http.Request.Host}/b1s/v1/$metadata#B1Sessions/@Element";
        return SapResults.Json(new LoginResponse(metadata, sessionId, expected.Version, expected.SessionTimeoutMinutes));
    }

    private static IResult Logout(HttpContext http, ISessionStore sessions)
    {
        if (http.Request.Cookies.TryGetValue(SessionRequiredFilter.CookieName, out var sessionId))
        {
            sessions.Remove(sessionId);
        }

        return Results.NoContent();
    }

    private sealed record LoginRequest(string? CompanyDB, string? UserName, string? Password);

    private sealed record LoginResponse(
        [property: JsonPropertyName("odata.metadata")] string Metadata,
        string SessionId,
        string Version,
        int SessionTimeout);
}
