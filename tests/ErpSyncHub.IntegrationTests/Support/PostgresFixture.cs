using Testcontainers.PostgreSql;

namespace ErpSyncHub.IntegrationTests.Support;

/// <summary>
/// Un PostgreSQL real compartido por todos los tests de integración. Usa la misma versión que Docker Compose.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17").Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync() => await _container.StartAsync();

    public async Task DisposeAsync() => await _container.DisposeAsync();
}

[CollectionDefinition(PostgresCollectionDefinition.Name)]
public sealed class PostgresCollectionDefinition : ICollectionFixture<PostgresFixture>
{
    public const string Name = "Postgres";
}
