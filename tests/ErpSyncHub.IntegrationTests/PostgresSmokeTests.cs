using ErpSyncHub.IntegrationTests.Support;
using Npgsql;

namespace ErpSyncHub.IntegrationTests;

[Collection(PostgresCollectionDefinition.Name)]
public class PostgresSmokeTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Testcontainers_levanta_un_PostgreSQL_17_alcanzable_desde_los_tests()
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SHOW server_version", connection);

        var version = (string?)await command.ExecuteScalarAsync();

        version.ShouldNotBeNull().ShouldStartWith("17");
    }
}
