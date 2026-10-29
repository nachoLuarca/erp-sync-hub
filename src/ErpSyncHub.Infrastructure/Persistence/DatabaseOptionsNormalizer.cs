using Microsoft.Extensions.Options;

namespace ErpSyncHub.Infrastructure.Persistence;

/// <summary>
/// Convierte al iniciar una cadena de conexión en formato URL al formato de Npgsql, para que el resto del código vea siempre el mismo.
/// Si la URL es inválida la deja intacta: <see cref="DatabaseOptionsValidator"/> la rechaza con un mensaje que no la revela.
/// </summary>
internal sealed class DatabaseOptionsNormalizer : IPostConfigureOptions<DatabaseOptions>
{
    public void PostConfigure(string? name, DatabaseOptions options)
    {
        if (PostgresConnectionString.TryNormalize(options.ConnectionString, out var normalized, out _))
        {
            options.ConnectionString = normalized;
        }
    }
}
