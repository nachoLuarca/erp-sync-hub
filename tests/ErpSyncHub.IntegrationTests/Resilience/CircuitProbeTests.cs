using ErpSyncHub.Application.Resilience;
using ErpSyncHub.Domain.Resilience;
using ErpSyncHub.IntegrationTests.Support;

namespace ErpSyncHub.IntegrationTests.Resilience;

/// <summary>La prueba semiabierta: una sola en toda la flota, con enfriamiento exponencial, y su resultado cierra o reabre el circuito.</summary>
[Collection(PostgresCollectionDefinition.Name)]
public class CircuitProbeTests(PostgresFixture postgres)
{
    private static Func<CancellationToken, Task<CircuitProbeResult>> Passing(Action? onRun = null) =>
        _ =>
        {
            onRun?.Invoke();
            return Task.FromResult(CircuitProbeResult.Success);
        };

    [Fact]
    public async Task Antes_del_enfriamiento_no_se_hace_la_prueba()
    {
        using var hub = CircuitHarness.For(await postgres.CreateMigratedDatabaseAsync());
        await hub.OpenAsync();
        var ran = false;

        hub.Time.Advance(TimeSpan.FromSeconds(10)); // el enfriamiento es de 30 s
        var probed = await hub.ProbeAsync(Passing(() => ran = true));

        probed.ShouldBeFalse();
        ran.ShouldBeFalse();
        (await hub.GetAsync()).State.ShouldBe(CircuitState.Open);
    }

    [Fact]
    public async Task Con_el_circuito_cerrado_no_hay_nada_que_probar()
    {
        using var hub = CircuitHarness.For(await postgres.CreateMigratedDatabaseAsync());
        var ran = false;

        (await hub.ProbeAsync(Passing(() => ran = true))).ShouldBeFalse();

        ran.ShouldBeFalse();
    }

    [Fact]
    public async Task Una_prueba_que_pasa_cierra_el_circuito_y_la_cola_se_reanuda()
    {
        using var hub = CircuitHarness.For(await postgres.CreateMigratedDatabaseAsync());
        await hub.OpenAsync();
        hub.Time.Advance(TimeSpan.FromSeconds(31));

        (await hub.ProbeAsync(Passing())).ShouldBeTrue();

        var snapshot = await hub.GetAsync();
        snapshot.State.ShouldBe(CircuitState.Closed);
        snapshot.AllowsWork.ShouldBeTrue();
        snapshot.OpenCount.ShouldBe(0);
        snapshot.ConsecutiveFailures.ShouldBe(0);
        (await hub.UseAsync(service => service.AllowsWorkAsync(CircuitNames.Sap, CancellationToken.None))).ShouldBeTrue();
    }

    [Fact]
    public async Task Una_prueba_fallida_reabre_el_circuito_con_el_doble_de_enfriamiento_hasta_el_tope()
    {
        using var hub = CircuitHarness.For(await postgres.CreateMigratedDatabaseAsync());
        await hub.OpenAsync();
        var failing = (Func<CancellationToken, Task<CircuitProbeResult>>)(_ => Task.FromResult(CircuitProbeResult.Failure("sap.unreachable", "Sin conexión.")));

        var expectedCooldowns = new[] { 60, 120, 240, 480, 900, 900 }; // segundos: se duplica y se acota en 15 minutos
        for (var n = 0; n < expectedCooldowns.Length; n++)
        {
            hub.Time.Advance((await hub.GetAsync()).NextProbeAt!.Value - hub.Time.GetUtcNow());
            (await hub.ProbeAsync(failing)).ShouldBeTrue();

            var snapshot = await hub.GetAsync();
            snapshot.State.ShouldBe(CircuitState.Open);
            snapshot.OpenCount.ShouldBe(n + 2);
            (snapshot.NextProbeAt!.Value - hub.Time.GetUtcNow()).ShouldBe(TimeSpan.FromSeconds(expectedCooldowns[n]));
        }
    }

    [Fact]
    public async Task Una_prueba_que_falla_por_credenciales_pasa_al_enfriamiento_largo()
    {
        using var hub = CircuitHarness.For(await postgres.CreateMigratedDatabaseAsync());
        await hub.OpenAsync();
        hub.Time.Advance(TimeSpan.FromSeconds(31));

        await hub.ProbeAsync(_ => Task.FromResult(CircuitProbeResult.Failure("sap.invalid_credentials", "Fail to get DB credentials", credentialsRejected: true)));

        var snapshot = await hub.GetAsync();
        snapshot.Reason.ShouldBe(CircuitOpenReason.CredentialsRejected);
        (snapshot.NextProbeAt!.Value - hub.Time.GetUtcNow()).ShouldBe(TimeSpan.FromMinutes(30)); // 15 minutos por dos
    }

    [Fact]
    public async Task Muchas_instancias_que_piden_la_prueba_a_la_vez_la_ejecutan_una_sola_vez()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        using var first = CircuitHarness.For(connectionString);
        await first.OpenAsync();
        first.Time.Advance(TimeSpan.FromSeconds(31));
        var hubs = Enumerable.Range(0, 8).Select(_ => CircuitHarness.For(connectionString, time: first.Time)).ToList();
        var executions = 0;

        try
        {
            var results = await Task.WhenAll(hubs.Select(hub => hub.ProbeAsync(Passing(() =>
            {
                Interlocked.Increment(ref executions);
                Thread.Sleep(200); // la prueba tarda: las demás llegan mientras corre
            }))));

            executions.ShouldBe(1);
            results.Count(ran => ran).ShouldBe(1);
            (await first.GetAsync()).State.ShouldBe(CircuitState.Closed);
        }
        finally
        {
            hubs.ForEach(hub => hub.Dispose());
        }
    }

    [Fact]
    public async Task Una_prueba_que_lanza_una_excepcion_cuenta_como_fallida_y_no_rompe_al_worker()
    {
        using var hub = CircuitHarness.For(await postgres.CreateMigratedDatabaseAsync());
        await hub.OpenAsync();
        hub.Time.Advance(TimeSpan.FromSeconds(31));

        var probed = await hub.ProbeAsync(_ => throw new InvalidOperationException("bug en la prueba"));

        probed.ShouldBeTrue();
        var snapshot = await hub.GetAsync();
        snapshot.State.ShouldBe(CircuitState.Open);
        snapshot.OpenCount.ShouldBe(2);
        snapshot.LastErrorCode.ShouldBe("circuit.probe_error");
    }

    [Fact]
    public async Task Una_prueba_que_no_termina_dentro_de_su_reserva_cuenta_como_fallida()
    {
        using var hub = CircuitHarness.For(await postgres.CreateMigratedDatabaseAsync(), options => options.ProbeLease = TimeSpan.FromMilliseconds(150));
        await hub.OpenAsync();
        hub.Time.Advance(TimeSpan.FromSeconds(31));

        await hub.ProbeAsync(async token =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), token);
            return CircuitProbeResult.Success;
        });

        var snapshot = await hub.GetAsync();
        snapshot.State.ShouldBe(CircuitState.Open);
        snapshot.LastErrorCode.ShouldBe("circuit.probe_timeout");
    }

    [Fact]
    public async Task Si_el_hub_se_apaga_a_mitad_de_la_prueba_otra_instancia_la_retoma_al_vencer_la_reserva()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        using var first = CircuitHarness.For(connectionString);
        using var second = CircuitHarness.For(connectionString, time: first.Time);
        await first.OpenAsync();
        first.Time.Advance(TimeSpan.FromSeconds(31));
        using var shutdown = new CancellationTokenSource();

        await Should.ThrowAsync<OperationCanceledException>(() => first.UseAsync(service => service.RunProbeIfDueAsync(
            CircuitNames.Sap,
            async token =>
            {
                await shutdown.CancelAsync();
                token.ThrowIfCancellationRequested();
                return CircuitProbeResult.Success;
            },
            shutdown.Token)));

        (await first.GetAsync()).State.ShouldBe(CircuitState.HalfOpen); // quedó a medias, con su reserva vigente
        (await second.ProbeAsync(Passing())).ShouldBeFalse(); // todavía no vence

        first.Time.Advance(TimeSpan.FromSeconds(31));
        (await second.ProbeAsync(Passing())).ShouldBeTrue();
        (await second.GetAsync()).State.ShouldBe(CircuitState.Closed);
    }

    [Fact]
    public async Task Una_persona_puede_cerrar_el_circuito_sin_esperar_la_prueba()
    {
        using var hub = CircuitHarness.For(await postgres.CreateMigratedDatabaseAsync());
        await hub.OpenAsync();

        var closed = await hub.UseAsync(service => service.CloseAsync(CircuitNames.Sap, CancellationToken.None));

        closed.State.ShouldBe(CircuitState.Closed);
        (await hub.ProbeAsync(Passing())).ShouldBeFalse();
        (await hub.GetAsync()).AllowsWork.ShouldBeTrue();
    }
}
