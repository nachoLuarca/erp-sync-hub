using Npgsql;

namespace ErpSyncHub.IntegrationTests.Support;

/// <summary>Consultas SQL directas a la base de datos de una prueba, por ejemplo después de apagar la Api.</summary>
internal static class Sql
{
    public static async Task<T?> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return await reader.IsDBNullAsync(0) ? default : reader.GetFieldValue<T>(0);
    }
}
