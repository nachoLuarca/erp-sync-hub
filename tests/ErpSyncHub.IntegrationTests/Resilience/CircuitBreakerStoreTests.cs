using ErpSyncHub.Application.Resilience;
using ErpSyncHub.Domain.Resilience;
using ErpSyncHub.IntegrationTests.Support;
using Microsoft.Extensions.Logging;

namespace ErpSyncHub.IntegrationTests.Resilience;

/// <summary>El circuito se guarda en PostgreSQL: lo comparten todas las instancias y sus cambios no se pisan.</summary>
[Collection(PostgresCollectionDefinition.Name)]
public class CircuitBreakerStoreTests(PostgresFixture postgres)
{
    private static readonly CircuitSettings Settings = new CircuitBreakerOptions().ToSettings();

    [Fact]
    public async Task Un_circuito_que_nunca_se_uso_esta_cerrado_y_consultarlo_no_crea_nada()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        using var hub = CircuitHarness.For(connectionString);

        var snapshot = await hub.GetAsync();

        snapshot.State.ShouldBe(CircuitState.Closed);
        snapshot.AllowsWork.ShouldBeTrue();
        (await Sql.ScalarAsync<long>(connectionString, "SELECT count(*) FROM circuit_breakers")).ShouldBe(0);
    }

    [Fact]
    public async Task El_estado_abierto_se_guarda_y_lo_ve_otra_instancia()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        using var first = CircuitHarness.For(connectionString);
        using var second = CircuitHarness.For(connectionString, time: first.Time);

        await first.OpenAsync();

        var seenBySecond = await second.GetAsync();
        seenBySecond.State.ShouldBe(CircuitState.Open);
        seenBySecond.Reason.ShouldBe(CircuitOpenReason.TransientFailures);
        seenBySecond.OpenCount.ShouldBe(1);
        seenBySecond.LastErrorCode.ShouldBe("sap.unreachable");
        (await second.UseAsync(service => service.AllowsWorkAsync(CircuitNames.Sap, CancellationToken.None))).ShouldBeFalse();
        (await Sql.ScalarAsync<string>(connectionString, "SELECT state FROM circuit_breakers")).ShouldBe("open");
    }

    [Fact]
    public async Task Las_fallas_de_dos_instancias_se_suman_en_la_misma_racha()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        using var first = CircuitHarness.For(connectionString);
        using var second = CircuitHarness.For(connectionString, time: first.Time);

        await first.FailAsync(2, TimeSpan.FromSeconds(10));
        await second.FailAsync(2, TimeSpan.FromSeconds(10));
        (await first.GetAsync()).State.ShouldBe(CircuitState.Closed);
        await second.FailAsync(1, TimeSpan.FromSeconds(10));

        (await first.GetAsync()).State.ShouldBe(CircuitState.Open);
    }

    [Fact]
    public async Task Fallas_simultaneas_de_muchos_workers_abren_el_circuito_una_sola_vez_sin_perder_la_cuenta()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        using var hub = CircuitHarness.For(connectionString);
        var now = hub.Time.GetUtcNow();

        var transitions = await Task.WhenAll(
            Enumerable.Range(0, 12).Select(_ => hub.UseStoreAsync(store =>
                store.UpdateAsync(CircuitNames.Sap, circuit => circuit.RecordTransientFailure("sap.unreachable", "x", now, Settings), CancellationToken.None))));

        transitions.Count(transition => transition is not null).ShouldBe(1); // abrió exactamente una vez
        var snapshot = await hub.GetAsync();
        snapshot.State.ShouldBe(CircuitState.Open);
        snapshot.ConsecutiveFailures.ShouldBe(5); // las siete restantes llegaron con el circuito ya abierto y no sumaron
    }

    [Fact]
    public async Task Con_muchas_instancias_pidiendo_la_prueba_a_la_vez_solo_una_la_obtiene()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        using var hub = CircuitHarness.For(connectionString);
        await hub.OpenAsync();
        hub.Time.Advance(TimeSpan.FromMinutes(2));
        var now = hub.Time.GetUtcNow();

        var began = await Task.WhenAll(
            Enumerable.Range(0, 12).Select(_ => hub.UseStoreAsync(store =>
                store.UpdateAsync(CircuitNames.Sap, circuit => circuit.TryBeginProbe(now, Settings), CancellationToken.None))));

        began.Count(transition => transition is not null).ShouldBe(1);
        (await hub.GetAsync()).State.ShouldBe(CircuitState.HalfOpen);
    }

    [Fact]
    public async Task Un_exito_reinicia_la_racha_guardada()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        using var hub = CircuitHarness.For(connectionString);
        await hub.FailAsync(4, TimeSpan.FromSeconds(1));
        (await hub.GetAsync()).ConsecutiveFailures.ShouldBe(4);

        await hub.UseAsync(service => service.ReportSuccessAsync(CircuitNames.Sap, known: null, CancellationToken.None));

        (await hub.GetAsync()).ConsecutiveFailures.ShouldBe(0);
        await hub.FailAsync(4, TimeSpan.FromSeconds(1));
        (await hub.GetAsync()).State.ShouldBe(CircuitState.Closed);
    }

    [Fact]
    public async Task Informar_un_exito_con_el_circuito_sano_no_escribe_nada()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        using var hub = CircuitHarness.For(connectionString);

        await hub.UseAsync(service => service.ReportSuccessAsync(CircuitNames.Sap, known: null, CancellationToken.None));

        (await Sql.ScalarAsync<long>(connectionString, "SELECT count(*) FROM circuit_breakers")).ShouldBe(0);
    }

    [Fact]
    public async Task Los_cambios_de_estado_dejan_una_linea_de_log_cada_uno()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        using var hub = CircuitHarness.For(connectionString);

        await hub.OpenAsync();
        hub.Time.Advance(TimeSpan.FromMinutes(1));
        await hub.ProbeAsync(_ => Task.FromResult(CircuitProbeResult.Success));

        var records = hub.Logs.Records.Where(record => record.Message.Contains("Circuito", StringComparison.Ordinal)).ToList();
        records.Select(record => record.Level).ShouldBe([LogLevel.Warning, LogLevel.Information, LogLevel.Information]);
        records[0].Message.ShouldContain("ABIERTO");
        records[1].Message.ShouldContain("semiabierto");
        records[2].Message.ShouldContain("CERRADO");
    }

    [Fact]
    public async Task Deshabilitado_por_configuracion_nunca_se_abre()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        using var hub = CircuitHarness.For(connectionString, options => options.Enabled = false);

        await hub.FailAsync(20, TimeSpan.FromSeconds(1));

        (await hub.GetAsync()).State.ShouldBe(CircuitState.Closed);
        (await hub.UseAsync(service => service.AllowsWorkAsync(CircuitNames.Sap, CancellationToken.None))).ShouldBeTrue();
    }

    [Fact]
    public async Task La_base_de_datos_acepta_los_tres_estados_y_rechaza_cualquier_otro()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();

        foreach (var state in new[] { "closed", "open", "half_open" })
        {
            await Sql.ExecuteAsync(connectionString, $"INSERT INTO circuit_breakers (name, state, consecutive_failures, open_count, updated_at) VALUES ('c-{state}', '{state}', 0, 0, now())");
        }

        await Should.ThrowAsync<Npgsql.PostgresException>(() =>
            Sql.ExecuteAsync(connectionString, "INSERT INTO circuit_breakers (name, state, consecutive_failures, open_count, updated_at) VALUES ('malo', 'abierto', 0, 0, now())"));
    }
}
