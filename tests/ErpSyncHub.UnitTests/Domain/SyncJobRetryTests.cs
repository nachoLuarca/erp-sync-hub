using ErpSyncHub.Domain.Common;
using ErpSyncHub.Domain.Jobs;

namespace ErpSyncHub.UnitTests.Domain;

public class SyncJobRetryTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);
    private static readonly ResultError Transient = ResultError.Transient("sap.unreachable", "SAP no responde.");

    private static SyncJob NewJob() =>
        SyncJob.Create(SyncSources.WooCommerce, "5501", SyncJobTypes.Order, "{}", 29990m, Now).Value;

    private static SyncJob Claimed(string workerId = "worker-1")
    {
        var job = NewJob();
        job.Claim(workerId, Now, Lease).IsSuccess.ShouldBeTrue();
        return job;
    }

    [Fact]
    public void Tomar_el_job_cuenta_el_intento_en_el_ciclo_y_en_el_historial()
    {
        var job = NewJob();

        job.Claim("worker-1", Now, Lease);

        job.AttemptCount.ShouldBe(1);
        job.CycleAttempts.ShouldBe(1);
    }

    [Fact]
    public void ScheduleRetry_deja_el_job_esperando_con_el_motivo_y_sin_dueno()
    {
        var job = Claimed();
        var next = Now.AddMinutes(1);

        var result = job.ScheduleRetry("worker-1", Transient, next, Now.AddSeconds(2));

        result.IsSuccess.ShouldBeTrue();
        job.Status.ShouldBe(JobStatus.Retrying);
        job.NextAttemptAt.ShouldBe(next);
        job.LastErrorCode.ShouldBe("sap.unreachable");
        job.LastErrorMessage.ShouldBe("SAP no responde.");
        job.LockedBy.ShouldBeNull();
        job.LockedUntil.ShouldBeNull();
        job.FinishedAt.ShouldBeNull();
        job.UpdatedAt.ShouldBe(Now.AddSeconds(2));
    }

    [Fact]
    public void Un_job_en_espera_no_se_puede_tomar_antes_de_su_hora_y_si_despues()
    {
        var job = Claimed();
        job.ScheduleRetry("worker-1", Transient, Now.AddMinutes(1), Now);

        job.IsClaimable(Now.AddSeconds(59)).ShouldBeFalse();
        job.IsClaimable(Now.AddMinutes(1)).ShouldBeTrue();
        job.IsClaimable(Now.AddHours(1)).ShouldBeTrue();
        job.Claim("worker-2", Now.AddSeconds(30), Lease).Error!.Code.ShouldBe("job.not_claimable");
        job.Claim("worker-2", Now.AddMinutes(1), Lease).IsSuccess.ShouldBeTrue();
        job.Status.ShouldBe(JobStatus.Processing);
        job.NextAttemptAt.ShouldBeNull();
        job.AttemptCount.ShouldBe(2);
        job.CycleAttempts.ShouldBe(2);
    }

    [Fact]
    public void Solo_el_dueno_del_job_puede_programar_el_reintento()
    {
        var job = Claimed("worker-1");

        job.ScheduleRetry("worker-2", Transient, Now.AddMinutes(1), Now).Error!.Code.ShouldBe("job.lost_lease");
        job.Status.ShouldBe(JobStatus.Processing);
    }

    [Fact]
    public void ScheduleRetry_exige_que_el_job_este_en_proceso()
    {
        NewJob().ScheduleRetry("worker-1", Transient, Now, Now).Error!.Code.ShouldBe("job.not_processing");
    }

    [Fact]
    public void MarkDeadLetter_cierra_el_job_con_el_motivo()
    {
        var job = Claimed();
        var finished = Now.AddMinutes(3);

        var result = job.MarkDeadLetter("worker-1", Transient, finished);

        result.IsSuccess.ShouldBeTrue();
        job.Status.ShouldBe(JobStatus.DeadLetter);
        job.LastErrorCode.ShouldBe("sap.unreachable");
        job.NextAttemptAt.ShouldBeNull();
        job.FinishedAt.ShouldBe(finished);
        job.LockedBy.ShouldBeNull();
        job.IsClaimable(finished.AddDays(1)).ShouldBeFalse();
    }

    [Fact]
    public void MarkDeadLetter_trunca_los_mensajes_demasiado_largos()
    {
        var job = Claimed();

        job.MarkDeadLetter("worker-1", ResultError.Transient(new string('c', 500), new string('m', 5000)), Now);

        job.LastErrorCode!.Length.ShouldBe(SyncJob.MaxErrorCodeLength);
        job.LastErrorMessage!.Length.ShouldBe(SyncJob.MaxErrorMessageLength);
    }

    [Fact]
    public void Solo_el_dueno_puede_mandar_el_job_al_dead_letter()
    {
        Claimed("worker-1").MarkDeadLetter("worker-2", Transient, Now).Error!.Code.ShouldBe("job.lost_lease");
    }

    [Fact]
    public void Un_error_definitivo_deja_el_job_fallido_y_no_se_vuelve_a_tomar()
    {
        var job = Claimed();

        job.MarkFailed("worker-1", ResultError.Rejected("order.unknown_sku", "SKU sin mapeo."), Now);

        job.Status.ShouldBe(JobStatus.Failed);
        job.IsClaimable(Now.AddDays(30)).ShouldBeFalse();
    }

    [Theory]
    [InlineData(JobStatus.Failed)]
    [InlineData(JobStatus.DeadLetter)]
    [InlineData(JobStatus.Retrying)]
    public void RetryNow_deja_disponible_el_job_con_un_presupuesto_de_intentos_nuevo(JobStatus from)
    {
        var job = JobIn(from);
        var attempts = job.AttemptCount;
        var retried = Now.AddHours(2);

        var result = job.RetryNow(retried);

        result.IsSuccess.ShouldBeTrue();
        job.Status.ShouldBe(JobStatus.Pending);
        job.CycleAttempts.ShouldBe(0);
        job.NextAttemptAt.ShouldBeNull();
        job.FinishedAt.ShouldBeNull();
        job.UpdatedAt.ShouldBe(retried);
        job.AttemptCount.ShouldBe(attempts); // el historial no se borra
        job.LastErrorCode.ShouldNotBeNull(); // el último error sigue a la vista hasta que haya otro resultado
        job.IsClaimable(retried).ShouldBeTrue();
    }

    [Fact]
    public void RetryNow_sobre_un_job_pendiente_es_inocuo()
    {
        var job = NewJob();

        job.RetryNow(Now.AddMinutes(1)).IsSuccess.ShouldBeTrue();

        job.Status.ShouldBe(JobStatus.Pending);
    }

    [Fact]
    public void RetryNow_no_se_aplica_a_un_job_en_proceso()
    {
        var job = Claimed();

        var result = job.RetryNow(Now);

        result.Error!.Code.ShouldBe("job.not_retryable");
        result.Error.Kind.ShouldBe(ErrorKind.Conflict);
        job.Status.ShouldBe(JobStatus.Processing);
        job.LockedBy.ShouldBe("worker-1");
    }

    [Fact]
    public void RetryNow_no_se_aplica_a_un_job_que_ya_termino_con_exito()
    {
        var job = Claimed();
        job.MarkSucceeded("worker-1", new SapOrderReference(7, 1007, "C1", 100m), Now);

        var result = job.RetryNow(Now);

        result.Error!.Code.ShouldBe("job.not_retryable");
        job.Status.ShouldBe(JobStatus.Succeeded);
    }

    [Fact]
    public void Un_reintento_manual_reinicia_el_ciclo_pero_el_historial_sigue_creciendo()
    {
        var job = Claimed();
        job.MarkDeadLetter("worker-1", Transient, Now);

        job.RetryNow(Now);
        job.Claim("worker-2", Now, Lease);

        job.AttemptCount.ShouldBe(2);
        job.CycleAttempts.ShouldBe(1);
    }

    [Fact]
    public void Terminar_con_exito_despues_de_reintentos_limpia_el_ultimo_error()
    {
        var job = Claimed();
        job.ScheduleRetry("worker-1", Transient, Now, Now);
        job.Claim("worker-2", Now, Lease);

        job.MarkSucceeded("worker-2", new SapOrderReference(7, 1007, "C1", 100m), Now.AddMinutes(1));

        job.Status.ShouldBe(JobStatus.Succeeded);
        job.LastErrorCode.ShouldBeNull();
        job.LastErrorMessage.ShouldBeNull();
        job.AttemptCount.ShouldBe(2);
    }

    [Fact]
    public void Los_intentos_abandonados_por_lease_vencido_cuentan_en_el_ciclo()
    {
        var job = Claimed("worker-1");

        job.Claim("worker-2", Now + Lease + TimeSpan.FromSeconds(1), Lease);

        job.CycleAttempts.ShouldBe(2);
        job.AttemptCount.ShouldBe(2);
    }

    private static SyncJob JobIn(JobStatus status)
    {
        var job = Claimed();
        var error = status == JobStatus.Failed ? ResultError.Rejected("order.unknown_sku", "SKU sin mapeo.") : Transient;

        _ = status switch
        {
            JobStatus.Failed => job.MarkFailed("worker-1", error, Now),
            JobStatus.DeadLetter => job.MarkDeadLetter("worker-1", error, Now),
            _ => job.ScheduleRetry("worker-1", error, Now.AddHours(1), Now),
        };

        job.Status.ShouldBe(status);
        return job;
    }
}
