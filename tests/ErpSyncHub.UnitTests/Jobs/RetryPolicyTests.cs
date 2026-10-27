using ErpSyncHub.Application.Jobs;
using ErpSyncHub.Domain.Common;

namespace ErpSyncHub.UnitTests.Jobs;

public class RetryPolicyTests
{
    private static readonly ResultError Transient = ResultError.Transient("sap.unreachable", "SAP no responde.");

    private static RetryPolicy Policy(Action<JobRetryOptions>? configure = null, double random = 1.0)
    {
        var options = new JobRetryOptions();
        configure?.Invoke(options);
        return new RetryPolicy(options, () => random);
    }

    [Fact]
    public void Los_valores_por_defecto_son_12_intentos_30_segundos_y_tope_de_1_hora()
    {
        var options = new JobRetryOptions();

        options.MaxAttempts.ShouldBe(12);
        options.BaseDelay.ShouldBe(TimeSpan.FromSeconds(30));
        options.MaxDelay.ShouldBe(TimeSpan.FromHours(1));
        options.Validate().ShouldBeEmpty();
    }

    [Fact]
    public void La_espera_se_duplica_en_cada_intento_hasta_el_tope()
    {
        var policy = Policy();

        var delays = Enumerable.Range(1, 11).Select(attempt => policy.DelayAfter(attempt)).ToList();

        delays.ShouldBe(
        [
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(60),
            TimeSpan.FromMinutes(2),
            TimeSpan.FromMinutes(4),
            TimeSpan.FromMinutes(8),
            TimeSpan.FromMinutes(16),
            TimeSpan.FromMinutes(32),
            TimeSpan.FromHours(1),
            TimeSpan.FromHours(1),
            TimeSpan.FromHours(1),
            TimeSpan.FromHours(1),
        ]);
    }

    [Theory]
    [InlineData(0.0, 0.5)]
    [InlineData(0.5, 0.75)]
    [InlineData(1.0, 1.0)]
    public void El_jitter_deja_la_espera_entre_el_50_y_el_100_por_ciento(double random, double fraction)
    {
        var policy = Policy(random: random);

        policy.DelayAfter(3).ShouldBe(TimeSpan.FromTicks((long)(TimeSpan.FromMinutes(2).Ticks * fraction)));
    }

    [Fact]
    public void Un_azar_fuera_de_rango_no_saca_la_espera_de_sus_limites()
    {
        Policy(random: -5).DelayAfter(1).ShouldBe(TimeSpan.FromSeconds(15));
        Policy(random: 7).DelayAfter(1).ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Un_exponente_enorme_no_desborda_y_queda_en_el_tope()
    {
        Policy().DelayAfter(5000).ShouldBe(TimeSpan.FromHours(1));
    }

    [Fact]
    public void Con_jitter_real_la_espera_nunca_sale_del_rango()
    {
        var random = new Random(1234);
        var policy = new RetryPolicy(new JobRetryOptions(), random.NextDouble);

        for (var attempt = 1; attempt <= 11; attempt++)
        {
            var ceiling = Policy().DelayAfter(attempt);
            var delay = policy.DelayAfter(attempt);

            delay.ShouldBeGreaterThanOrEqualTo(ceiling / 2);
            delay.ShouldBeLessThanOrEqualTo(ceiling);
        }
    }

    [Fact]
    public void Un_error_transitorio_se_reintenta_con_la_espera_calculada()
    {
        var decision = Policy().Decide(Transient, attemptsMade: 2);

        decision.Action.ShouldBe(RetryAction.RetryLater);
        decision.Delay.ShouldBe(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void Un_error_inesperado_tambien_se_reintenta()
    {
        var decision = Policy().Decide(ResultError.Unexpected("job.unexpected", "Falló algo."), attemptsMade: 1);

        decision.Action.ShouldBe(RetryAction.RetryLater);
    }

    [Theory]
    [InlineData(ErrorKind.Rejected)]
    [InlineData(ErrorKind.Validation)]
    [InlineData(ErrorKind.NotFound)]
    [InlineData(ErrorKind.Conflict)]
    public void Un_error_definitivo_no_se_reintenta_aunque_queden_intentos(ErrorKind kind)
    {
        var decision = Policy().Decide(new ResultError("order.unknown_sku", "El SKU no tiene mapeo.", kind), attemptsMade: 1);

        decision.Action.ShouldBe(RetryAction.Fail);
        decision.Delay.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public void Al_agotar_los_intentos_un_error_transitorio_va_al_dead_letter()
    {
        var policy = Policy(options => options.MaxAttempts = 3);

        policy.Decide(Transient, attemptsMade: 1).Action.ShouldBe(RetryAction.RetryLater);
        policy.Decide(Transient, attemptsMade: 2).Action.ShouldBe(RetryAction.RetryLater);
        var last = policy.Decide(Transient, attemptsMade: 3);

        last.Action.ShouldBe(RetryAction.DeadLetter);
        last.Delay.ShouldBe(TimeSpan.Zero);
        policy.Decide(Transient, attemptsMade: 4).Action.ShouldBe(RetryAction.DeadLetter);
    }

    [Fact]
    public void Con_un_solo_intento_permitido_el_primer_fallo_transitorio_va_directo_al_dead_letter()
    {
        Policy(options => options.MaxAttempts = 1).Decide(Transient, attemptsMade: 1).Action.ShouldBe(RetryAction.DeadLetter);
    }

    [Fact]
    public void Un_error_definitivo_no_va_al_dead_letter_ni_siquiera_en_el_ultimo_intento()
    {
        var decision = Policy(options => options.MaxAttempts = 2).Decide(ResultError.Rejected("x", "y"), attemptsMade: 2);

        decision.Action.ShouldBe(RetryAction.Fail);
    }

    [Fact]
    public void Decidir_exige_un_intento_hecho()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => Policy().Decide(Transient, attemptsMade: 0));
    }

    [Fact]
    public void La_ventana_total_con_los_valores_por_defecto_va_de_unas_dos_horas_y_media_a_algo_mas_de_cinco()
    {
        var (min, max) = Policy().TotalWindow();

        // 30 s + 1 min + 2 + 4 + 8 + 16 + 32 min + 4 × 1 h = 5 h 3 min 30 s (sin jitter); con jitter, la mitad como mínimo.
        max.ShouldBe(new TimeSpan(5, 3, 30));
        min.ShouldBe(new TimeSpan(2, 31, 45));
    }

    [Fact]
    public void La_ventana_con_un_solo_intento_es_cero()
    {
        Policy(options => options.MaxAttempts = 1).TotalWindow().ShouldBe((TimeSpan.Zero, TimeSpan.Zero));
    }

    [Theory]
    [InlineData(0, "MaxAttempts")]
    [InlineData(101, "MaxAttempts")]
    public void Un_maximo_de_intentos_fuera_de_rango_se_rechaza(int attempts, string expected)
    {
        var errors = new JobRetryOptions { MaxAttempts = attempts }.Validate();

        errors.ShouldHaveSingleItem().ShouldContain(expected);
        errors[0].ShouldContain("Worker__Retry__MaxAttempts");
    }

    [Fact]
    public void Una_espera_base_fuera_de_rango_se_rechaza()
    {
        new JobRetryOptions { BaseDelay = TimeSpan.Zero }.Validate().ShouldContain(error => error.Contains("BaseDelay", StringComparison.Ordinal));
        new JobRetryOptions { BaseDelay = TimeSpan.FromDays(2), MaxDelay = TimeSpan.FromDays(1) }.Validate()
            .ShouldContain(error => error.Contains("BaseDelay", StringComparison.Ordinal));
    }

    [Fact]
    public void El_tope_no_puede_ser_menor_que_la_espera_base_ni_superar_un_dia()
    {
        new JobRetryOptions { BaseDelay = TimeSpan.FromMinutes(5), MaxDelay = TimeSpan.FromMinutes(1) }.Validate()
            .ShouldHaveSingleItem().ShouldContain("MaxDelay");
        new JobRetryOptions { MaxDelay = TimeSpan.FromDays(3) }.Validate()
            .ShouldHaveSingleItem().ShouldContain("MaxDelay");
    }

    [Fact]
    public void Una_configuracion_pequena_para_pruebas_es_valida()
    {
        new JobRetryOptions { MaxAttempts = 4, BaseDelay = TimeSpan.FromMilliseconds(20), MaxDelay = TimeSpan.FromMilliseconds(100) }
            .Validate().ShouldBeEmpty();
    }
}
