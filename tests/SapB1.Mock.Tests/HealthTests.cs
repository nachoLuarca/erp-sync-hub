using Microsoft.AspNetCore.Mvc.Testing;

namespace SapB1.Mock.Tests;

public class HealthTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Health_responde_200()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        response.IsSuccessStatusCode.ShouldBeTrue();
    }
}
