using ErpSyncHub.Application.Resilience;

namespace ErpSyncHub.UnitTests.Jobs;

public class CircuitBreakerOptionsTests
{
    [Fact]
    public void Los_valores_por_defecto_son_validos_y_son_los_documentados()
    {
        var options = new CircuitBreakerOptions();

        options.Validate().ShouldBeEmpty();
        options.Enabled.ShouldBeTrue();
        options.FailureThreshold.ShouldBe(5);
        options.FailureWindow.ShouldBe(TimeSpan.FromMinutes(5));
        options.CooldownBase.ShouldBe(TimeSpan.FromSeconds(30));
        options.CooldownMax.ShouldBe(TimeSpan.FromMinutes(15));
        options.CredentialsCooldownBase.ShouldBe(TimeSpan.FromMinutes(15));
        options.CredentialsCooldownMax.ShouldBe(TimeSpan.FromHours(1));
    }

    [Theory]
    [InlineData(0, "Circuit__FailureThreshold")]
    [InlineData(101, "ERPSYNC_CIRCUIT_FAILURE_THRESHOLD")]
    public void Un_umbral_fuera_de_rango_nombra_la_variable(int threshold, string expected)
    {
        var errors = new CircuitBreakerOptions { FailureThreshold = threshold }.Validate();

        errors.ShouldHaveSingleItem().ShouldContain(expected);
    }

    [Fact]
    public void Una_ventana_menor_que_un_segundo_o_mayor_que_un_dia_es_invalida()
    {
        new CircuitBreakerOptions { FailureWindow = TimeSpan.FromMilliseconds(500) }.Validate().ShouldHaveSingleItem().ShouldContain("Circuit__FailureWindow");
        new CircuitBreakerOptions { FailureWindow = TimeSpan.FromDays(2) }.Validate().ShouldHaveSingleItem().ShouldContain("ERPSYNC_CIRCUIT_FAILURE_WINDOW");
    }

    [Fact]
    public void El_tope_del_enfriamiento_no_puede_ser_menor_que_su_base()
    {
        var errors = new CircuitBreakerOptions { CooldownBase = TimeSpan.FromMinutes(2), CooldownMax = TimeSpan.FromMinutes(1) }.Validate();

        errors.ShouldHaveSingleItem().ShouldContain("Circuit__CooldownMax");
    }

    [Fact]
    public void El_enfriamiento_por_credenciales_se_valida_aparte()
    {
        var errors = new CircuitBreakerOptions { CredentialsCooldownBase = TimeSpan.FromHours(2), CredentialsCooldownMax = TimeSpan.FromHours(1) }.Validate();

        errors.ShouldHaveSingleItem().ShouldContain("ERPSYNC_CIRCUIT_CREDENTIALS_COOLDOWN_MAX");
    }

    [Theory]
    [InlineData(4)]
    [InlineData(601)]
    public void La_reserva_de_la_prueba_tiene_limites(int seconds)
    {
        new CircuitBreakerOptions { ProbeLease = TimeSpan.FromSeconds(seconds) }.Validate().ShouldHaveSingleItem().ShouldContain("Circuit__ProbeLease");
    }
}
