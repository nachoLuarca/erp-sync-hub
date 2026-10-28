namespace ErpSyncHub.Domain.Admin;

/// <summary>
/// Una acción manual hecha desde el panel: quién (la huella de su clave, nunca la clave), qué y sobre qué. El registro es de solo
/// inserción: no hay forma de modificar ni de borrar una entrada.
/// </summary>
public sealed class AdminAuditEntry
{
    public const int MaxActionLength = 50;
    public const int MaxTargetLength = 200;
    public const int MaxFingerprintLength = 16;
    public const int MaxRoleLength = 20;
    public const int MaxOutcomeLength = 50;

    // Para EF Core.
    private AdminAuditEntry()
    {
    }

    public Guid Id { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    /// <summary>La acción, por ejemplo <c>job.retry</c>.</summary>
    public string Action { get; private set; } = string.Empty;

    /// <summary>Sobre qué se hizo (un job, un mapeo, un circuito); <see langword="null"/> si la acción no tiene un objeto.</summary>
    public string? Target { get; private set; }

    /// <summary>Huella corta de la clave que se usó.</summary>
    public string KeyFingerprint { get; private set; } = string.Empty;

    /// <summary>Nivel de la clave: <c>read</c> u <c>operate</c>.</summary>
    public string Role { get; private set; } = string.Empty;

    /// <summary>Cómo terminó, por ejemplo <c>retried</c>, <c>not_found</c> o <c>conflict</c>.</summary>
    public string Outcome { get; private set; } = string.Empty;

    /// <summary>Crea la entrada; los textos que exceden su largo máximo se recortan en vez de perder el registro.</summary>
    public static AdminAuditEntry Create(
        DateTimeOffset occurredAt,
        string action,
        string? target,
        string keyFingerprint,
        string role,
        string outcome)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);

        return new AdminAuditEntry
        {
            Id = Guid.NewGuid(),
            OccurredAt = occurredAt,
            Action = Truncate(action, MaxActionLength),
            Target = string.IsNullOrWhiteSpace(target) ? null : Truncate(target, MaxTargetLength),
            KeyFingerprint = Truncate(keyFingerprint, MaxFingerprintLength),
            Role = Truncate(role, MaxRoleLength),
            Outcome = Truncate(outcome, MaxOutcomeLength),
        };
    }

    private static string Truncate(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];
}
