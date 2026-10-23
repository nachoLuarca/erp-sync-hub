using ErpSyncHub.Domain.Common;
using ErpSyncHub.Domain.Jobs;

namespace ErpSyncHub.UnitTests.Domain;

public class SyncAttemptTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Un_intento_nuevo_esta_en_progreso()
    {
        var attempt = NewAttempt();

        attempt.Outcome.ShouldBe(AttemptOutcome.InProgress);
        attempt.FinishedAt.ShouldBeNull();
        attempt.ErrorCode.ShouldBeNull();
        attempt.ErrorKind.ShouldBeNull();
        attempt.Id.Version.ShouldBe(7);
    }

    [Fact]
    public void Succeed_cierra_el_intento_con_exito()
    {
        var attempt = NewAttempt();
        var finished = Now + TimeSpan.FromSeconds(4);

        var result = attempt.Succeed(finished);

        result.IsSuccess.ShouldBeTrue();
        attempt.Outcome.ShouldBe(AttemptOutcome.Succeeded);
        attempt.FinishedAt.ShouldBe(finished);
        attempt.ErrorCode.ShouldBeNull();
    }

    [Fact]
    public void Fail_guarda_el_error_y_su_clasificacion()
    {
        var attempt = NewAttempt();

        var result = attempt.Fail(ResultError.Transient("sap.unavailable", "SAP no responde."), Now);

        result.IsSuccess.ShouldBeTrue();
        attempt.Outcome.ShouldBe(AttemptOutcome.Failed);
        attempt.ErrorCode.ShouldBe("sap.unavailable");
        attempt.ErrorMessage.ShouldBe("SAP no responde.");
        attempt.ErrorKind.ShouldBe(ErrorKind.Transient);
    }

    [Fact]
    public void Fail_trunca_los_mensajes_demasiado_largos()
    {
        var attempt = NewAttempt();

        attempt.Fail(ResultError.Unexpected(new string('c', 500), new string('m', 5000)), Now);

        attempt.ErrorCode!.Length.ShouldBe(SyncJob.MaxErrorCodeLength);
        attempt.ErrorMessage!.Length.ShouldBe(SyncJob.MaxErrorMessageLength);
    }

    [Fact]
    public void Abandon_marca_el_intento_de_un_worker_que_dejo_de_responder()
    {
        var attempt = NewAttempt();

        attempt.Abandon(Now + TimeSpan.FromMinutes(6)).IsSuccess.ShouldBeTrue();

        attempt.Outcome.ShouldBe(AttemptOutcome.Abandoned);
        attempt.FinishedAt.ShouldBe(Now + TimeSpan.FromMinutes(6));
    }

    [Fact]
    public void Un_intento_cerrado_no_se_puede_cerrar_de_nuevo()
    {
        var attempt = NewAttempt();
        attempt.Succeed(Now);

        attempt.Fail(ResultError.Unexpected("x", "y"), Now).Error!.Code.ShouldBe("attempt.already_closed");
        attempt.Abandon(Now).Error!.Code.ShouldBe("attempt.already_closed");
        attempt.Succeed(Now).Error!.Code.ShouldBe("attempt.already_closed");
        attempt.Outcome.ShouldBe(AttemptOutcome.Succeeded);
    }

    [Fact]
    public void Fail_exige_un_error()
    {
        Should.Throw<ArgumentNullException>(() => NewAttempt().Fail(null!, Now));
    }

    private static SyncAttempt NewAttempt()
    {
        var job = SyncJob.Create(SyncSources.WooCommerce, "5501", SyncJobTypes.Order, "{}", null, Now).Value;
        return job.Claim("worker-1", Now, TimeSpan.FromMinutes(5)).Value;
    }
}
