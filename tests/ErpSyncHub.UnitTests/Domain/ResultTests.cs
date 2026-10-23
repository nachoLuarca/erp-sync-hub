using ErpSyncHub.Domain.Common;

namespace ErpSyncHub.UnitTests.Domain;

public class ResultTests
{
    [Fact]
    public void Success_no_tiene_error()
    {
        var result = Result.Success();

        result.IsSuccess.ShouldBeTrue();
        result.IsFailure.ShouldBeFalse();
        result.Error.ShouldBeNull();
    }

    [Fact]
    public void Failure_conserva_el_error()
    {
        var error = ResultError.Rejected("sap.rejected", "SAP rechazó la orden.");

        var result = Result.Failure(error);

        result.IsFailure.ShouldBeTrue();
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(error);
    }

    [Fact]
    public void Success_con_valor_lo_expone()
    {
        var result = Result.Success(42);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(42);
    }

    [Fact]
    public void Un_resultado_fallido_no_tiene_valor()
    {
        var result = Result.Failure<int>(ResultError.NotFound("x.not_found", "No existe."));

        result.IsFailure.ShouldBeTrue();
        var exception = Should.Throw<InvalidOperationException>(() => result.Value);
        exception.Message.ShouldContain("x.not_found");
    }

    [Fact]
    public void Un_resultado_con_valor_es_tambien_un_Result()
    {
        Result result = Result.Failure<string>(ResultError.Conflict("x.conflict", "Choca."));

        result.Error!.Kind.ShouldBe(ErrorKind.Conflict);
    }

    [Fact]
    public void Failure_rechaza_un_error_nulo()
    {
        Should.Throw<ArgumentNullException>(() => Result.Failure(null!));
        Should.Throw<ArgumentNullException>(() => Result.Failure<int>(null!));
    }

    [Theory]
    [InlineData(ErrorKind.Transient, true)]
    [InlineData(ErrorKind.Rejected, false)]
    [InlineData(ErrorKind.Validation, false)]
    [InlineData(ErrorKind.NotFound, false)]
    [InlineData(ErrorKind.Conflict, false)]
    [InlineData(ErrorKind.Unexpected, false)]
    public void Solo_los_errores_pasajeros_son_reintentables(ErrorKind kind, bool expected)
    {
        new ResultError("x", "y", kind).IsTransient.ShouldBe(expected);
    }

    [Fact]
    public void Las_fabricas_de_Error_asignan_su_clasificacion()
    {
        ResultError.Validation("a", "m").Kind.ShouldBe(ErrorKind.Validation);
        ResultError.NotFound("a", "m").Kind.ShouldBe(ErrorKind.NotFound);
        ResultError.Conflict("a", "m").Kind.ShouldBe(ErrorKind.Conflict);
        ResultError.Rejected("a", "m").Kind.ShouldBe(ErrorKind.Rejected);
        ResultError.Transient("a", "m").Kind.ShouldBe(ErrorKind.Transient);
        ResultError.Unexpected("a", "m").Kind.ShouldBe(ErrorKind.Unexpected);
    }
}
