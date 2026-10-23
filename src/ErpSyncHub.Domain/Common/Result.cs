namespace ErpSyncHub.Domain.Common;

/// <summary>Resultado de una operación que puede fallar por una razón de negocio, sin recurrir a excepciones.</summary>
public class Result
{
    private protected Result(ResultError? error) => Error = error;

    public bool IsSuccess => Error is null;

    public bool IsFailure => !IsSuccess;

    /// <summary>El error, o <see langword="null"/> si la operación tuvo éxito.</summary>
    public ResultError? Error { get; }

    public static Result Success() => new(null);

    public static Result Failure(ResultError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Result(error);
    }

    public static Result<T> Success<T>(T value) => new(value, null);

    public static Result<T> Failure<T>(ResultError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Result<T>(default, error);
    }
}

/// <summary>Resultado de una operación que, si tiene éxito, produce un valor.</summary>
public sealed class Result<T> : Result
{
    private readonly T? _value;

    internal Result(T? value, ResultError? error)
        : base(error) => _value = value;

    /// <summary>El valor producido. Leerlo en un resultado fallido es un error de programación.</summary>
    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException($"El resultado falló y no tiene valor: {Error!.Code}.");
}
