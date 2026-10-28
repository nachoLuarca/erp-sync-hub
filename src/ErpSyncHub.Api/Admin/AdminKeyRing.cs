using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace ErpSyncHub.Api.Admin;

/// <summary>Nivel de acceso de una clave del panel.</summary>
internal enum AdminRole
{
    /// <summary>Consulta jobs, detalle y métricas.</summary>
    Read,

    /// <summary>Además de leer, ejecuta acciones: reintentar, sincronizar, cerrar el circuito y eliminar mapeos.</summary>
    Operate,
}

/// <summary>Quien presentó una clave válida.</summary>
/// <param name="Role">Nivel de acceso de la clave.</param>
/// <param name="Fingerprint">Huella corta de la clave (ver <see cref="AdminKeyRing.FingerprintLength"/>): identifica cuál se usó sin revelarla.</param>
internal sealed record AdminCaller(AdminRole Role, string Fingerprint);

/// <summary>
/// Las claves configuradas del panel. Comprueba la que presenta un cliente en tiempo constante: no depende de en qué posición
/// difieren las claves ni de cuántas coincidan, de modo que no filtra información por el tiempo de respuesta.
/// </summary>
/// <remarks>
/// Cada clave se reduce a su SHA-256 (de largo fijo, así que tampoco filtra el largo) y los hashes se comparan con
/// <see cref="CryptographicOperations.FixedTimeEquals"/>. Se recorren <b>todas</b> las claves aunque una ya haya coincidido. La
/// huella son los primeros caracteres de ese mismo hash: con claves aleatorias de 32 caracteres o más no permite recuperarla.
/// </remarks>
internal sealed class AdminKeyRing
{
    /// <summary>Caracteres hexadecimales de la huella (32 bits): para distinguir claves en una auditoría, no para identificarlas de forma única.</summary>
    public const int FingerprintLength = 8;

    private readonly (byte[] Hash, AdminCaller Caller)[] _entries;

    public AdminKeyRing(IOptions<AdminApiKeyOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _entries =
        [
            .. options.Value.ReadKeys.Select(key => Entry(key, AdminRole.Read)),
            .. options.Value.OperateKeys.Select(key => Entry(key, AdminRole.Operate)),
        ];
    }

    /// <summary>Falso si no hay ninguna clave configurada: el panel está deshabilitado.</summary>
    public bool IsConfigured => _entries.Length > 0;

    /// <summary>Cantidad de claves de lectura.</summary>
    public int ReadKeyCount => _entries.Count(entry => entry.Caller.Role == AdminRole.Read);

    /// <summary>Cantidad de claves de operación.</summary>
    public int OperateKeyCount => _entries.Count(entry => entry.Caller.Role == AdminRole.Operate);

    /// <summary>Quien corresponde a la clave presentada, o <see langword="null"/> si no es ninguna de las configuradas.</summary>
    public AdminCaller? Authenticate(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate))
        {
            return null;
        }

        var candidateHash = Hash(candidate);

        AdminCaller? found = null;
        foreach (var (hash, caller) in _entries)
        {
            if (CryptographicOperations.FixedTimeEquals(candidateHash, hash))
            {
                found = caller;
            }
        }

        return found;
    }

    private static (byte[] Hash, AdminCaller Caller) Entry(string key, AdminRole role)
    {
        var hash = Hash(key);
        return (hash, new AdminCaller(role, Convert.ToHexStringLower(hash)[..FingerprintLength]));
    }

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
}
