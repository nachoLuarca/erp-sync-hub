using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace ErpSyncHub.Api.Admin;

/// <summary>
/// Comprueba la clave que presenta un cliente del panel contra las configuradas. La comparación es en tiempo constante: no
/// depende de en qué posición difieren las claves ni de cuántas coincidan, de modo que no filtra información por el tiempo
/// de respuesta.
/// </summary>
/// <remarks>
/// Cada clave se reduce a su SHA-256 (de largo fijo, así que tampoco filtra el largo) y los hashes se comparan con
/// <see cref="CryptographicOperations.FixedTimeEquals"/>. Se recorren <b>todas</b> las claves aunque una ya haya coincidido, y
/// se usa <c>|</c> y no <c>||</c> para que no haya cortocircuito.
/// </remarks>
internal sealed class AdminApiKeyValidator
{
    private readonly byte[][] _keyHashes;

    public AdminApiKeyValidator(IOptions<AdminApiKeyOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _keyHashes = [.. options.Value.Keys.Select(Hash)];
    }

    /// <summary>Falso si no hay ninguna clave configurada: el panel está deshabilitado.</summary>
    public bool IsConfigured => _keyHashes.Length > 0;

    /// <summary>Cantidad de claves configuradas.</summary>
    public int KeyCount => _keyHashes.Length;

    /// <summary>Verdadero si <paramref name="candidate"/> es una de las claves configuradas.</summary>
    public bool IsValid(string? candidate)
    {
        var candidateHash = Hash(candidate ?? string.Empty);

        var matched = false;
        foreach (var keyHash in _keyHashes)
        {
            matched |= CryptographicOperations.FixedTimeEquals(candidateHash, keyHash);
        }

        return matched && !string.IsNullOrEmpty(candidate);
    }

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
}
