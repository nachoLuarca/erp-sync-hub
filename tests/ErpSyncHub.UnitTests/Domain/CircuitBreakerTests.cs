using ErpSyncHub.Domain.Resilience;

namespace ErpSyncHub.UnitTests.Domain;

public class CircuitBreakerTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    private static readonly CircuitSettings Settings = new(
        FailureThreshold: 5,
        FailureWindow: TimeSpan.FromMinutes(5),
        CooldownBase: TimeSpan.FromSeconds(30),
        CooldownMax: TimeSpan.FromMinutes(15),
        CredentialsCooldownBase: TimeSpan.FromMinutes(15),
        CredentialsCooldownMax: TimeSpan.FromHours(1),
        ProbeLease: TimeSpan.FromSeconds(30));

    private static CircuitBreaker NewCircuit() => CircuitBreaker.Create("sap", Start);

    private static CircuitTransition? Fail(CircuitBreaker circuit, TimeSpan after, CircuitSettings? settings = null) =>
        circuit.RecordTransientFailure("sap.unreachable", "Sin conexión.", Start + after, settings ?? Settings);

    private static CircuitBreaker OpenCircuit()
    {
        var circuit = NewCircuit();
        for (var n = 0; n < 5; n++)
        {
            Fail(circuit, TimeSpan.FromSeconds(n));
        }

        return circuit;
    }

    // --- Apertura por fallas sostenidas ---

    [Fact]
    public void Un_circuito_nuevo_esta_cerrado_y_deja_pasar_el_trabajo()
    {
        var circuit = NewCircuit();

        circuit.State.ShouldBe(CircuitState.Closed);
        circuit.AllowsWork.ShouldBeTrue();
    }

    [Fact]
    public void Se_abre_con_la_quinta_falla_consecutiva_dentro_de_la_ventana_y_no_antes()
    {
        var circuit = NewCircuit();

        for (var n = 0; n < 4; n++)
        {
            Fail(circuit, TimeSpan.FromSeconds(n * 10)).ShouldBeNull();
            circuit.AllowsWork.ShouldBeTrue();
        }

        var transition = Fail(circuit, TimeSpan.FromSeconds(40));

        transition.ShouldNotBeNull();
        transition.From.ShouldBe(CircuitState.Closed);
        transition.To.ShouldBe(CircuitState.Open);
        transition.Reason.ShouldBe(CircuitOpenReason.TransientFailures);
        transition.OpenCount.ShouldBe(1);
        transition.NextProbeAt.ShouldBe(Start + TimeSpan.FromSeconds(40) + TimeSpan.FromSeconds(30));
        circuit.AllowsWork.ShouldBeFalse();
    }

    [Fact]
    public void Cualquier_exito_reinicia_la_racha()
    {
        var circuit = NewCircuit();

        for (var n = 0; n < 4; n++)
        {
            Fail(circuit, TimeSpan.FromSeconds(n));
        }

        circuit.RecordSuccess(Start + TimeSpan.FromSeconds(5));
        circuit.ConsecutiveFailures.ShouldBe(0);

        for (var n = 0; n < 4; n++)
        {
            Fail(circuit, TimeSpan.FromSeconds(10 + n)).ShouldBeNull();
        }

        circuit.State.ShouldBe(CircuitState.Closed);
    }

    [Fact]
    public void Las_fallas_que_no_caben_en_la_ventana_empiezan_una_racha_nueva()
    {
        var circuit = NewCircuit();

        // Cuatro fallas, y la quinta llega pasados los 5 minutos desde la primera: no son 5 consecutivas dentro de la ventana.
        Fail(circuit, TimeSpan.Zero);
        Fail(circuit, TimeSpan.FromMinutes(1));
        Fail(circuit, TimeSpan.FromMinutes(2));
        Fail(circuit, TimeSpan.FromMinutes(3));
        Fail(circuit, TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1)).ShouldBeNull();

        circuit.State.ShouldBe(CircuitState.Closed);
        circuit.ConsecutiveFailures.ShouldBe(1);
    }

    [Fact]
    public void Con_poco_trafico_y_backoff_la_ventana_de_cinco_minutos_lo_abre_y_una_de_un_minuto_no_lo_abriria_nunca()
    {
        // Pocos jobs reintentando con su backoff: las fallas llegan espaciadas (aquí, 40, 60, 90 y 90 segundos entre una y otra).
        TimeSpan[] failureTimes = [TimeSpan.Zero, TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(100), TimeSpan.FromSeconds(190), TimeSpan.FromSeconds(280)];

        var fiveMinutes = NewCircuit();
        var oneMinute = NewCircuit();
        var oneMinuteSettings = Settings with { FailureWindow = TimeSpan.FromMinutes(1) };
        CircuitTransition? opened = null;
        foreach (var time in failureTimes)
        {
            opened = Fail(fiveMinutes, time);
            Fail(oneMinute, time, oneMinuteSettings);
        }

        opened.ShouldNotBeNull();
        fiveMinutes.State.ShouldBe(CircuitState.Open);
        oneMinute.State.ShouldBe(CircuitState.Closed); // la racha se reinicia cada vez que pasa el minuto: nunca llega a 5
    }

    [Fact]
    public void Un_error_definitivo_de_SAP_no_es_una_falla_y_el_exito_reinicia_la_racha()
    {
        var circuit = NewCircuit();
        Fail(circuit, TimeSpan.Zero);
        Fail(circuit, TimeSpan.FromSeconds(1));

        // SAP respondió con sentido (por ejemplo, rechazó un dato): quien llama lo informa como éxito de la conexión.
        circuit.RecordSuccess(Start + TimeSpan.FromSeconds(2));

        circuit.ConsecutiveFailures.ShouldBe(0);
        circuit.FirstFailureAt.ShouldBeNull();
    }

    [Fact]
    public void Con_el_circuito_abierto_las_fallas_y_los_exitos_de_trabajos_en_vuelo_no_cambian_nada()
    {
        var circuit = OpenCircuit();
        var openedAt = circuit.OpenedAt;
        var nextProbe = circuit.NextProbeAt;

        Fail(circuit, TimeSpan.FromSeconds(10)).ShouldBeNull();
        circuit.RecordSuccess(Start + TimeSpan.FromSeconds(11));

        circuit.State.ShouldBe(CircuitState.Open);
        circuit.OpenedAt.ShouldBe(openedAt);
        circuit.NextProbeAt.ShouldBe(nextProbe);
    }

    // --- Credenciales ---

    [Fact]
    public void Las_credenciales_rechazadas_lo_abren_de_inmediato_con_un_enfriamiento_largo()
    {
        var circuit = NewCircuit();

        var transition = circuit.RecordCredentialsRejected("sap.invalid_credentials", "Fail to get DB credentials", Start, Settings);

        transition.ShouldNotBeNull();
        transition.To.ShouldBe(CircuitState.Open);
        transition.Reason.ShouldBe(CircuitOpenReason.CredentialsRejected);
        transition.NextProbeAt.ShouldBe(Start + TimeSpan.FromMinutes(15));
        circuit.ConsecutiveFailures.ShouldBe(1);
    }

    // --- Prueba semiabierta ---

    [Fact]
    public void La_prueba_no_toca_antes_del_enfriamiento_y_toca_al_cumplirse()
    {
        var circuit = OpenCircuit();
        var probeAt = circuit.NextProbeAt!.Value;

        circuit.IsProbeDue(probeAt - TimeSpan.FromSeconds(1)).ShouldBeFalse();
        circuit.TryBeginProbe(probeAt - TimeSpan.FromSeconds(1), Settings).ShouldBeNull();
        circuit.State.ShouldBe(CircuitState.Open);

        circuit.IsProbeDue(probeAt).ShouldBeTrue();
        var transition = circuit.TryBeginProbe(probeAt, Settings);

        transition.ShouldNotBeNull();
        transition.From.ShouldBe(CircuitState.Open);
        transition.To.ShouldBe(CircuitState.HalfOpen);
        circuit.State.ShouldBe(CircuitState.HalfOpen);
        circuit.AllowsWork.ShouldBeFalse(); // semiabierto sigue sin dejar pasar trabajo
    }

    [Fact]
    public void Solo_una_instancia_reserva_la_prueba_mientras_su_reserva_este_vigente()
    {
        var circuit = OpenCircuit();
        var probeAt = circuit.NextProbeAt!.Value;

        circuit.TryBeginProbe(probeAt, Settings).ShouldNotBeNull();

        circuit.TryBeginProbe(probeAt + TimeSpan.FromSeconds(5), Settings).ShouldBeNull(); // otra instancia llega mientras la primera prueba
        circuit.TryBeginProbe(probeAt + TimeSpan.FromSeconds(29), Settings).ShouldBeNull();
    }

    [Fact]
    public void Si_quien_hacia_la_prueba_cae_otra_instancia_la_retoma_al_vencer_la_reserva()
    {
        var circuit = OpenCircuit();
        var probeAt = circuit.NextProbeAt!.Value;
        circuit.TryBeginProbe(probeAt, Settings).ShouldNotBeNull();

        var retaken = circuit.TryBeginProbe(probeAt + TimeSpan.FromSeconds(31), Settings);

        retaken.ShouldNotBeNull();
        retaken.From.ShouldBe(CircuitState.HalfOpen);
        retaken.To.ShouldBe(CircuitState.HalfOpen);
        circuit.ProbeLeaseUntil.ShouldBe(probeAt + TimeSpan.FromSeconds(31) + TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Una_prueba_exitosa_cierra_el_circuito_y_olvida_la_racha_y_el_enfriamiento()
    {
        var circuit = OpenCircuit();
        var probeAt = circuit.NextProbeAt!.Value;
        circuit.TryBeginProbe(probeAt, Settings);

        var transition = circuit.CompleteProbeSuccess(probeAt + TimeSpan.FromSeconds(1));

        transition.ShouldNotBeNull();
        transition.To.ShouldBe(CircuitState.Closed);
        circuit.State.ShouldBe(CircuitState.Closed);
        circuit.AllowsWork.ShouldBeTrue();
        circuit.OpenCount.ShouldBe(0);
        circuit.ConsecutiveFailures.ShouldBe(0);
        circuit.NextProbeAt.ShouldBeNull();
        circuit.Reason.ShouldBeNull();
    }

    [Fact]
    public void Una_prueba_fallida_lo_vuelve_a_abrir_con_el_doble_de_enfriamiento()
    {
        var circuit = OpenCircuit();
        var probeAt = circuit.NextProbeAt!.Value;
        circuit.TryBeginProbe(probeAt, Settings);

        var transition = circuit.CompleteProbeFailure(credentialsRejected: false, "sap.unreachable", "Sin conexión.", probeAt, Settings);

        transition.ShouldNotBeNull();
        transition.From.ShouldBe(CircuitState.HalfOpen);
        transition.To.ShouldBe(CircuitState.Open);
        transition.OpenCount.ShouldBe(2);
        transition.NextProbeAt.ShouldBe(probeAt + TimeSpan.FromSeconds(60));
    }

    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(3, 120)]
    [InlineData(4, 240)]
    [InlineData(5, 480)]
    [InlineData(6, 900)] // 960 s, pero el tope es 15 minutos
    [InlineData(40, 900)]
    public void El_enfriamiento_crece_al_doble_hasta_el_tope(int openCount, int expectedSeconds)
    {
        CircuitBreaker.Cooldown(CircuitOpenReason.TransientFailures, openCount, Settings).ShouldBe(TimeSpan.FromSeconds(expectedSeconds));
    }

    [Theory]
    [InlineData(1, 15)]
    [InlineData(2, 30)]
    [InlineData(3, 60)]
    [InlineData(4, 60)]
    public void El_enfriamiento_por_credenciales_parte_en_15_minutos_y_se_acota_en_una_hora(int openCount, int expectedMinutes)
    {
        CircuitBreaker.Cooldown(CircuitOpenReason.CredentialsRejected, openCount, Settings).ShouldBe(TimeSpan.FromMinutes(expectedMinutes));
    }

    [Fact]
    public void Una_prueba_fallida_por_credenciales_pasa_al_enfriamiento_largo()
    {
        var circuit = OpenCircuit();
        var probeAt = circuit.NextProbeAt!.Value;
        circuit.TryBeginProbe(probeAt, Settings);

        var transition = circuit.CompleteProbeFailure(credentialsRejected: true, "sap.invalid_credentials", "Fail to get DB credentials", probeAt, Settings);

        transition!.Reason.ShouldBe(CircuitOpenReason.CredentialsRejected);
        transition.NextProbeAt.ShouldBe(probeAt + TimeSpan.FromMinutes(30)); // segunda apertura: 15 minutos por dos
    }

    // --- Cierre manual y casos sin efecto ---

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Una_persona_puede_cerrar_el_circuito_abierto_o_semiabierto(bool halfOpen)
    {
        var circuit = OpenCircuit();
        if (halfOpen)
        {
            circuit.TryBeginProbe(circuit.NextProbeAt!.Value, Settings);
        }

        var transition = circuit.Close(Start + TimeSpan.FromMinutes(1));

        transition.ShouldNotBeNull();
        transition.To.ShouldBe(CircuitState.Closed);
        circuit.AllowsWork.ShouldBeTrue();
        circuit.OpenCount.ShouldBe(0);
    }

    [Fact]
    public void Cerrar_un_circuito_cerrado_no_hace_nada()
    {
        NewCircuit().Close(Start).ShouldBeNull();
    }

    [Fact]
    public void El_resultado_de_una_prueba_sin_prueba_en_curso_se_ignora()
    {
        var circuit = OpenCircuit();

        circuit.CompleteProbeSuccess(Start).ShouldBeNull();
        circuit.CompleteProbeFailure(false, "x", null, Start, Settings).ShouldBeNull();
        circuit.State.ShouldBe(CircuitState.Open);
    }

    [Fact]
    public void Recuerda_el_ultimo_error_y_recorta_los_textos_largos()
    {
        var circuit = NewCircuit();

        circuit.RecordTransientFailure(new string('c', 500), new string('m', 5000), Start, Settings);

        circuit.LastErrorCode!.Length.ShouldBe(CircuitBreaker.MaxErrorCodeLength);
        circuit.LastErrorMessage!.Length.ShouldBe(CircuitBreaker.MaxErrorMessageLength);
    }
}
