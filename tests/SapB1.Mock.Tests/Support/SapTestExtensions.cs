using System.Net.Http.Json;
using System.Text.Json;

namespace SapB1.Mock.Tests.Support;

internal static class SapTestExtensions
{
    public const string CompanyDb = "SBODemoCL";
    public const string UserName = "manager";
    public const string Password = "manager";

    public static Task<HttpResponseMessage> LoginRawAsync(
        this HttpClient client,
        string companyDb = CompanyDb,
        string userName = UserName,
        string password = Password) =>
        client.PostAsJsonAsync("/b1s/v1/Login", new { CompanyDB = companyDb, UserName = userName, Password = password });

    /// <summary>Inicia sesión con las credenciales válidas y devuelve el identificador de sesión.</summary>
    public static async Task<string> LoginAsync(this HttpClient client)
    {
        var response = await client.LoginRawAsync();
        response.EnsureSuccessStatusCode();

        using var body = await ReadJsonAsync(response);
        return body.RootElement.GetProperty("SessionId").GetString()!;
    }

    public static Task<HttpResponseMessage> SendAsync(
        this HttpClient client,
        HttpMethod method,
        string url,
        string? sessionId = null,
        HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, url) { Content = content };
        if (sessionId is not null)
        {
            request.Headers.Add("Cookie", $"B1SESSION={sessionId}");
        }

        return client.SendAsync(request);
    }

    public static async Task<JsonDocument> ReadJsonAsync(this HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    /// <summary>Comprueba el formato de error del Service Layer y devuelve el código y el mensaje.</summary>
    public static async Task<(int Code, string Message)> ReadSapErrorAsync(this HttpResponseMessage response)
    {
        using var body = await response.ReadJsonAsync();
        var error = body.RootElement.GetProperty("error");
        var message = error.GetProperty("message");

        message.GetProperty("lang").GetString().ShouldBe("en-us");
        return (error.GetProperty("code").GetInt32(), message.GetProperty("value").GetString()!);
    }
}
