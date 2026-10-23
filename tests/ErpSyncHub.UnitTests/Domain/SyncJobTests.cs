using ErpSyncHub.Domain.Common;
using ErpSyncHub.Domain.Jobs;

namespace ErpSyncHub.UnitTests.Domain;

public class SyncJobTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);
    private static readonly SapOrderReference SapOrder = new(DocEntry: 7, DocNum: 1007, CardCode: "C12345678-5", Total: 24990m);

    [Fact]
    public void Create_deja_el_job_pendiente_con_sus_datos()
    {
        var result = SyncJob.Create(SyncSources.WooCommerce, "5501", SyncJobTypes.Order, "{\"id\":5501}", 29990m, Now);

        result.IsSuccess.ShouldBeTrue();
        var job = result.Value;
        job.Id.ShouldNotBe(Guid.Empty);
        job.Id.Version.ShouldBe(7);
        job.Source.ShouldBe("woocommerce");
        job.ExternalId.ShouldBe("5501");
        job.Type.ShouldBe("order");
        job.Payload.ShouldBe("{\"id\":5501}");
        job.Status.ShouldBe(JobStatus.Pending);
        job.AttemptCount.ShouldBe(0);
        job.StoreTotal.ShouldBe(29990m);
        job.SapTotal.ShouldBeNull();
        job.CreatedAt.ShouldBe(Now);
        job.UpdatedAt.ShouldBe(Now);
        job.FinishedAt.ShouldBeNull();
        job.LockedBy.ShouldBeNull();
        job.LockedUntil.ShouldBeNull();
    }

    [Theory]
    [InlineData("", "5501", "order", "{}")]
    [InlineData("woocommerce", " ", "order", "{}")]
    [InlineData("woocommerce", "5501", "", "{}")]
    [InlineData("woocommerce", "5501", "order", "")]
    public void Create_rechaza_campos_vacios(string source, string externalId, string type, string payload)
    {
        var result = SyncJob.Create(source, externalId, type, payload, null, Now);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Kind.ShouldBe(ErrorKind.Validation);
    }

    [Fact]
    public void Create_rechaza_campos_demasiado_largos()
    {
        SyncJob.Create(new string('s', SyncJob.MaxSourceLength + 1), "1", "order", "{}", null, Now).IsFailure.ShouldBeTrue();
        SyncJob.Create("woocommerce", new string('1', SyncJob.MaxExternalIdLength + 1), "order", "{}", null, Now).IsFailure.ShouldBeTrue();
        SyncJob.Create("woocommerce", "1", new string('t', SyncJob.MaxTypeLength + 1), "{}", null, Now).IsFailure.ShouldBeTrue();
        SyncJob.Create("woocommerce", new string('1', SyncJob.MaxExternalIdLength), "order", "{}", null, Now).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Claim_toma_el_job_y_registra_el_primer_intento()
    {
        var job = NewJob();

        var result = job.Claim("worker-1", Now, Lease);

        result.IsSuccess.ShouldBeTrue();
        job.Status.ShouldBe(JobStatus.Processing);
        job.AttemptCount.ShouldBe(1);
        job.LockedBy.ShouldBe("worker-1");
        job.LockedUntil.ShouldBe(Now + Lease);
        job.UpdatedAt.ShouldBe(Now);
        var attempt = result.Value;
        attempt.JobId.ShouldBe(job.Id);
        attempt.Number.ShouldBe(1);
        attempt.WorkerId.ShouldBe("worker-1");
        attempt.StartedAt.ShouldBe(Now);
        attempt.Outcome.ShouldBe(AttemptOutcome.InProgress);
    }

    [Fact]
    public void Un_job_con_lease_vigente_no_se_puede_tomar_otra_vez()
    {
        var job = NewJob();
        job.Claim("worker-1", Now, Lease);

        var result = job.Claim("worker-2", Now + TimeSpan.FromMinutes(1), Lease);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe("job.not_claimable");
        job.LockedBy.ShouldBe("worker-1");
        job.AttemptCount.ShouldBe(1);
    }

    [Fact]
    public void Un_job_con_el_lease_vencido_lo_retoma_otro_worker_con_un_nuevo_intento()
    {
        var job = NewJob();
        job.Claim("worker-1", Now, Lease);
        var later = Now + Lease + TimeSpan.FromSeconds(1);

        var result = job.Claim("worker-2", later, Lease);

        result.IsSuccess.ShouldBeTrue();
        job.LockedBy.ShouldBe("worker-2");
        job.LockedUntil.ShouldBe(later + Lease);
        job.AttemptCount.ShouldBe(2);
        result.Value.Number.ShouldBe(2);
    }

    [Fact]
    public void El_lease_vence_exactamente_en_LockedUntil()
    {
        var job = NewJob();
        job.Claim("worker-1", Now, Lease);

        job.IsClaimable(Now + Lease - TimeSpan.FromTicks(1)).ShouldBeFalse();
        job.IsClaimable(Now + Lease).ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Claim_exige_un_worker(string workerId)
    {
        NewJob().Claim(workerId, Now, Lease).Error!.Code.ShouldBe("job.invalid_worker");
    }

    [Fact]
    public void Claim_rechaza_un_worker_con_nombre_demasiado_largo()
    {
        NewJob().Claim(new string('w', SyncJob.MaxWorkerIdLength + 1), Now, Lease).IsFailure.ShouldBeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Claim_exige_un_lease_positivo(int minutes)
    {
        NewJob().Claim("worker-1", Now, TimeSpan.FromMinutes(minutes)).Error!.Code.ShouldBe("job.invalid_lease");
    }

    [Fact]
    public void MarkSucceeded_guarda_la_orden_de_SAP_y_libera_el_job()
    {
        var job = ClaimedJob("worker-1");
        var finished = Now + TimeSpan.FromSeconds(3);

        var result = job.MarkSucceeded("worker-1", SapOrder, finished);

        result.IsSuccess.ShouldBeTrue();
        job.Status.ShouldBe(JobStatus.Succeeded);
        job.SapDocEntry.ShouldBe(7);
        job.SapDocNum.ShouldBe(1007);
        job.SapCardCode.ShouldBe("C12345678-5");
        job.SapTotal.ShouldBe(24990m);
        job.LastErrorCode.ShouldBeNull();
        job.LockedBy.ShouldBeNull();
        job.LockedUntil.ShouldBeNull();
        job.FinishedAt.ShouldBe(finished);
        job.UpdatedAt.ShouldBe(finished);
        job.IsClaimable(finished + Lease).ShouldBeFalse();
    }

    [Fact]
    public void MarkFailed_guarda_el_motivo_y_libera_el_job()
    {
        var job = ClaimedJob("worker-1");
        var finished = Now + TimeSpan.FromSeconds(2);

        var result = job.MarkFailed("worker-1", ResultError.Rejected("sap.rejected", "El artículo no existe."), finished);

        result.IsSuccess.ShouldBeTrue();
        job.Status.ShouldBe(JobStatus.Failed);
        job.LastErrorCode.ShouldBe("sap.rejected");
        job.LastErrorMessage.ShouldBe("El artículo no existe.");
        job.LockedBy.ShouldBeNull();
        job.FinishedAt.ShouldBe(finished);
        job.IsClaimable(finished + Lease).ShouldBeFalse();
    }

    [Fact]
    public void MarkFailed_trunca_los_mensajes_demasiado_largos()
    {
        var job = ClaimedJob("worker-1");

        job.MarkFailed("worker-1", ResultError.Unexpected(new string('c', 500), new string('m', 5000)), Now);

        job.LastErrorCode!.Length.ShouldBe(SyncJob.MaxErrorCodeLength);
        job.LastErrorMessage!.Length.ShouldBe(SyncJob.MaxErrorMessageLength);
    }

    [Fact]
    public void Solo_el_worker_dueno_del_lease_puede_cerrar_el_job()
    {
        var job = ClaimedJob("worker-1");

        var succeeded = job.MarkSucceeded("worker-2", SapOrder, Now);
        var failed = job.MarkFailed("worker-2", ResultError.Unexpected("x", "y"), Now);

        succeeded.Error!.Code.ShouldBe("job.lost_lease");
        failed.Error!.Code.ShouldBe("job.lost_lease");
        job.Status.ShouldBe(JobStatus.Processing);
    }

    [Fact]
    public void Un_worker_que_perdio_el_lease_no_puede_cerrar_el_job_que_otro_retomo()
    {
        var job = ClaimedJob("worker-1");
        job.Claim("worker-2", Now + Lease + TimeSpan.FromSeconds(1), Lease);

        var result = job.MarkSucceeded("worker-1", SapOrder, Now + Lease + TimeSpan.FromSeconds(2));

        result.Error!.Code.ShouldBe("job.lost_lease");
        job.LockedBy.ShouldBe("worker-2");
    }

    [Fact]
    public void Un_job_pendiente_no_se_puede_cerrar()
    {
        var job = NewJob();

        job.MarkSucceeded("worker-1", SapOrder, Now).Error!.Code.ShouldBe("job.not_processing");
        job.MarkFailed("worker-1", ResultError.Unexpected("x", "y"), Now).Error!.Code.ShouldBe("job.not_processing");
    }

    [Fact]
    public void Un_job_cerrado_no_se_puede_cerrar_de_nuevo_ni_tomar()
    {
        var job = ClaimedJob("worker-1");
        job.MarkSucceeded("worker-1", SapOrder, Now);

        job.MarkFailed("worker-1", ResultError.Unexpected("x", "y"), Now).Error!.Code.ShouldBe("job.not_processing");
        job.Claim("worker-1", Now + Lease, Lease).Error!.Code.ShouldBe("job.not_claimable");
        job.Status.ShouldBe(JobStatus.Succeeded);
    }

    [Fact]
    public void Un_job_fallido_no_se_toma_solo_en_esta_fase()
    {
        var job = ClaimedJob("worker-1");
        job.MarkFailed("worker-1", ResultError.Transient("sap.unavailable", "SAP no responde."), Now);

        job.IsClaimable(Now + TimeSpan.FromDays(1)).ShouldBeFalse();
    }

    [Theory]
    [InlineData(29990, 29990, false)]
    [InlineData(29990, 29990.005, false)]
    [InlineData(29990, 24990, true)]
    [InlineData(24990, 29990, true)]
    [InlineData(100, 100.02, true)]
    public void HasTotalMismatch_compara_los_totales_con_tolerancia_de_redondeo(double store, double sap, bool expected)
    {
        var job = ClaimedJob("worker-1", storeTotal: (decimal)store);

        job.MarkSucceeded("worker-1", SapOrder with { Total = (decimal)sap }, Now);

        job.HasTotalMismatch.ShouldBe(expected);
    }

    [Fact]
    public void HasTotalMismatch_es_falso_si_falta_algun_total()
    {
        var withoutStoreTotal = ClaimedJob("worker-1", storeTotal: null);
        withoutStoreTotal.MarkSucceeded("worker-1", SapOrder, Now);

        NewJob().HasTotalMismatch.ShouldBeFalse();
        withoutStoreTotal.HasTotalMismatch.ShouldBeFalse();
    }

    [Fact]
    public void MarkSucceeded_exige_la_orden_de_SAP()
    {
        var job = ClaimedJob("worker-1");

        Should.Throw<ArgumentNullException>(() => job.MarkSucceeded("worker-1", null!, Now));
        Should.Throw<ArgumentNullException>(() => job.MarkFailed("worker-1", null!, Now));
    }

    private static SyncJob NewJob(decimal? storeTotal = 29990m) =>
        SyncJob.Create(SyncSources.WooCommerce, "5501", SyncJobTypes.Order, "{}", storeTotal, Now).Value;

    private static SyncJob ClaimedJob(string workerId, decimal? storeTotal = 29990m)
    {
        var job = NewJob(storeTotal);
        job.Claim(workerId, Now, Lease).IsSuccess.ShouldBeTrue();
        return job;
    }
}
