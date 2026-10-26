using ErpSyncHub.Application.Inventory;
using ErpSyncHub.Infrastructure.Persistence;
using Microsoft.Extensions.Options;
using Npgsql;

namespace ErpSyncHub.Infrastructure.Inventory;

/// <summary>
/// Exclusión mutua de la sincronización de stock y precios con un <i>advisory lock</i> de PostgreSQL (ADR 0009). El lock
/// vive en una conexión propia, sin pool, durante toda la ejecución: si la instancia muere, la base cierra la sesión y suelta
/// el lock sola, así que una caída nunca deja la sincronización bloqueada. Como el lock es de la base de datos, vale entre
/// todas las instancias del hub que la comparten.
/// </summary>
internal sealed class PostgresInventorySyncLock(IOptions<DatabaseOptions> options) : IInventorySyncLock
{
    /// <summary>Clave del lock: constante, para que todas las instancias compitan por el mismo (los bytes de «ERPSYNC» y un 1).</summary>
    public const long Key = 0x4552_5053_594E_4301L;

    public async Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken cancellationToken)
    {
        // Sin pool: cerrar la conexión termina la sesión y con ella el lock, aunque el desbloqueo explícito falle.
        var connectionString = new NpgsqlConnectionStringBuilder(options.Value.ConnectionString)
        {
            Pooling = false,
            TcpKeepAlive = true,
        }.ConnectionString;

        var connection = new NpgsqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);

            await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", connection);
            command.Parameters.AddWithValue("key", Key);
            if ((bool)(await command.ExecuteScalarAsync(cancellationToken))!)
            {
                return new Held(connection);
            }

            await connection.DisposeAsync();
            return null;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private sealed class Held(NpgsqlConnection connection) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", connection);
                command.Parameters.AddWithValue("key", Key);
                await command.ExecuteScalarAsync(CancellationToken.None);
            }
            catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException)
            {
                // La conexión ya estaba caída: la base soltó el lock al cerrar la sesión.
            }
            finally
            {
                await connection.DisposeAsync();
            }
        }
    }
}
