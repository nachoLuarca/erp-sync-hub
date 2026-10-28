using ErpSyncHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace ErpSyncHub.IntegrationTests.Support;

/// <summary>
/// Un PostgreSQL real compartido por todos los tests de integración (misma versión que Docker Compose).
/// Cada test recibe su propia base de datos, clonada de una plantilla ya migrada: es mucho más rápido que
/// migrar cada vez y mantiene los tests aislados entre sí.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime, IDisposable
{
    private const string TemplateName = "erpsynchub_template";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17").Build();
    private readonly SemaphoreSlim _templateGate = new(1, 1);
    private bool _templateReady;

    /// <summary>Variable que activa la conexión directa a la red de Docker (la define scripts/test-container.sh).</summary>
    public const string ContainerNetworkVariable = "ERPSYNC_TESTS_CONTAINER_NETWORK";

    /// <summary>
    /// Conexión administrativa al servidor (base <c>postgres</c>). Si las pruebas corren dentro de un contenedor (ver
    /// scripts/test.ps1), se conecta a la IP del contenedor de PostgreSQL por la red de Docker y no por el puerto publicado en el host:
    /// las pruebas abren miles de conexiones sin pool y el reenvío de puertos de Docker Desktop deja algunas colgadas hasta agotar el
    /// tiempo de espera, lo que hacía fallar pruebas al azar.
    /// </summary>
    public string AdminConnectionString =>
        Environment.GetEnvironmentVariable(ContainerNetworkVariable) == "1"
            ? new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Host = _container.IpAddress, Port = 5432 }.ConnectionString
            : _container.GetConnectionString();

    public async Task InitializeAsync() => await _container.StartAsync();

    public async Task DisposeAsync() => await _container.DisposeAsync();

    public void Dispose() => _templateGate.Dispose();

    /// <summary>Crea una base de datos con el esquema ya migrado y devuelve su cadena de conexión.</summary>
    public async Task<string> CreateMigratedDatabaseAsync()
    {
        await EnsureTemplateAsync();
        return await CreateDatabaseAsync(TemplateName);
    }

    /// <summary>Crea una base de datos vacía, sin tablas, y devuelve su cadena de conexión.</summary>
    public Task<string> CreateEmptyDatabaseAsync() => CreateDatabaseAsync(template: null);

    public static ErpSyncHubDbContext CreateContext(string connectionString) =>
        new(DbContextSetup.BuildOptions(connectionString));

    private async Task<string> CreateDatabaseAsync(string? template)
    {
        var name = $"test_{Guid.NewGuid():N}";
        var templateClause = template is null ? string.Empty : $" TEMPLATE \"{template}\"";
        await ExecuteAdminAsync($"CREATE DATABASE \"{name}\"{templateClause}");

        return new NpgsqlConnectionStringBuilder(AdminConnectionString) { Database = name, Pooling = false }.ConnectionString;
    }

    private async Task EnsureTemplateAsync()
    {
        await _templateGate.WaitAsync();
        try
        {
            if (_templateReady)
            {
                return;
            }

            await ExecuteAdminAsync($"CREATE DATABASE \"{TemplateName}\"");

            // Sin pool: una conexión abierta a la plantilla impediría clonarla.
            var connectionString = new NpgsqlConnectionStringBuilder(AdminConnectionString)
            {
                Database = TemplateName,
                Pooling = false,
            }.ConnectionString;

            await using var context = CreateContext(connectionString);
            await context.Database.MigrateAsync();

            _templateReady = true;
        }
        finally
        {
            _templateGate.Release();
        }
    }

    private async Task ExecuteAdminAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}

[CollectionDefinition(PostgresCollectionDefinition.Name)]
public sealed class PostgresCollectionDefinition : ICollectionFixture<PostgresFixture>
{
    public const string Name = "Postgres";
}
