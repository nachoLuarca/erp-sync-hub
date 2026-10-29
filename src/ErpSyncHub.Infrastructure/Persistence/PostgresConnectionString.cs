using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Npgsql;

namespace ErpSyncHub.Infrastructure.Persistence;

/// <summary>
/// Convierte la cadena de conexión en formato URL de PostgreSQL (<c>postgresql://usuario:clave@host/base?sslmode=require</c>), la que
/// entregan Neon y la mayoría de los servicios gestionados, al formato de Npgsql (<c>Host=…;Database=…</c>). Una cadena que ya está en
/// formato Npgsql pasa sin cambios.
/// </summary>
/// <remarks>
/// Los mensajes de error nunca incluyen la cadena recibida ni sus partes: la contraseña no puede terminar en un log.
/// </remarks>
public static class PostgresConnectionString
{
    private const string UrlHint =
        "Use el formato Npgsql (Host=…;Database=…;Username=…;Password=…) o una URL postgresql://usuario:clave@host[:puerto]/base[?sslmode=…]. " +
        "Los caracteres especiales de la contraseña van codificados con %.";

    /// <summary>Indica si el valor está en formato URL (<c>postgresql://</c> o <c>postgres://</c>).</summary>
    public static bool IsUrl(string? value)
    {
        var trimmed = value?.TrimStart();
        return trimmed is not null
            && (trimmed.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Devuelve la cadena en formato Npgsql; lanza <see cref="FormatException"/> (sin datos sensibles) si la URL es inválida.</summary>
    public static string Normalize(string connectionString)
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        return TryNormalize(connectionString, out var normalized, out var error)
            ? normalized
            : throw new FormatException(error);
    }

    /// <summary>Intenta convertir; si falla, <paramref name="error"/> describe el problema sin revelar la cadena.</summary>
    public static bool TryNormalize(string connectionString, [NotNullWhen(true)] out string? normalized, [NotNullWhen(false)] out string? error)
    {
        ArgumentNullException.ThrowIfNull(connectionString);

        if (!IsUrl(connectionString))
        {
            normalized = connectionString;
            error = null;
            return true;
        }

        return TryFromUrl(connectionString.Trim(), out normalized, out error);
    }

    private static bool TryFromUrl(string url, [NotNullWhen(true)] out string? normalized, [NotNullWhen(false)] out string? error)
    {
        normalized = null;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.IdnHost))
        {
            error = $"La URL de PostgreSQL no es válida (falta el host o tiene un formato incorrecto). {UrlHint}";
            return false;
        }

        var builder = new NpgsqlConnectionStringBuilder { Host = uri.IdnHost };

        if (!uri.IsDefaultPort)
        {
            builder.Port = uri.Port;
        }

        var database = Uri.UnescapeDataString(uri.AbsolutePath.Trim('/'));
        if (database.Length > 0)
        {
            builder.Database = database;
        }

        if (uri.UserInfo.Length > 0)
        {
            var separator = uri.UserInfo.IndexOf(':', StringComparison.Ordinal);
            var user = separator < 0 ? uri.UserInfo : uri.UserInfo[..separator];
            builder.Username = Uri.UnescapeDataString(user);
            if (separator >= 0)
            {
                builder.Password = Uri.UnescapeDataString(uri.UserInfo[(separator + 1)..]);
            }
        }

        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            var name = Uri.UnescapeDataString(separator < 0 ? pair : pair[..separator]).ToLowerInvariant();
            var value = separator < 0 ? string.Empty : Uri.UnescapeDataString(pair[(separator + 1)..]);

            if (!TryApplyParameter(builder, name, value, out error))
            {
                return false;
            }
        }

        normalized = builder.ConnectionString;
        error = null;
        return true;
    }

    private static bool TryApplyParameter(NpgsqlConnectionStringBuilder builder, string name, string value, [NotNullWhen(false)] out string? error)
    {
        error = null;
        switch (name)
        {
            case "sslmode":
                if (Enum.TryParse(value.Replace("-", string.Empty, StringComparison.Ordinal), ignoreCase: true, out SslMode sslMode)
                    && Enum.IsDefined(sslMode))
                {
                    builder.SslMode = sslMode;
                    return true;
                }

                error = "El parámetro sslmode de la URL de PostgreSQL debe ser disable, allow, prefer, require, verify-ca o verify-full.";
                return false;

            case "channel_binding":
                if (Enum.TryParse(value, ignoreCase: true, out ChannelBinding channelBinding) && Enum.IsDefined(channelBinding))
                {
                    builder.ChannelBinding = channelBinding;
                    return true;
                }

                error = "El parámetro channel_binding de la URL de PostgreSQL debe ser disable, prefer o require.";
                return false;

            case "connect_timeout":
                if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
                {
                    builder.Timeout = seconds;
                    return true;
                }

                error = "El parámetro connect_timeout de la URL de PostgreSQL debe ser un número entero de segundos.";
                return false;

            case "application_name":
                builder.ApplicationName = value;
                return true;

            default:
                error = $"El parámetro '{name}' de la URL de PostgreSQL no está soportado (se aceptan sslmode, channel_binding, connect_timeout y application_name). " +
                        "Para otros ajustes use el formato Npgsql.";
                return false;
        }
    }
}
