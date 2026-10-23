using ErpSyncHub.Domain.Common;
using ErpSyncHub.Domain.Customers;

namespace ErpSyncHub.Domain.Mappings;

/// <summary>Equivalencia entre un cliente de la tienda (ver <see cref="CustomerKey"/>) y su <c>CardCode</c> en SAP.</summary>
public sealed class CustomerMapping
{
    public const int MaxExternalKeyLength = 200;

    // Para EF Core.
    private CustomerMapping()
    {
    }

    public Guid Id { get; private set; }

    public string ExternalKey { get; private set; } = string.Empty;

    public string CardCode { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public static Result<CustomerMapping> Create(string externalKey, string cardCode, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(externalKey) || externalKey.Trim().Length > MaxExternalKeyLength)
        {
            return Result.Failure<CustomerMapping>(
                ResultError.Validation("mapping.invalid_key", $"La clave del cliente es obligatoria y admite hasta {MaxExternalKeyLength} caracteres."));
        }

        if (string.IsNullOrWhiteSpace(cardCode) || cardCode.Trim().Length > SapCardCodes.MaxLength)
        {
            return Result.Failure<CustomerMapping>(
                ResultError.Validation("mapping.invalid_card_code", $"El CardCode es obligatorio y admite hasta {SapCardCodes.MaxLength} caracteres."));
        }

        return Result.Success(new CustomerMapping
        {
            Id = Guid.CreateVersion7(now),
            ExternalKey = externalKey.Trim(),
            CardCode = cardCode.Trim(),
            CreatedAt = now,
        });
    }
}
