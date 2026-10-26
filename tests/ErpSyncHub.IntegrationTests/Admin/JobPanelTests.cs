using System.Net;
using System.Text.Json;
using ErpSyncHub.Domain.Common;
using ErpSyncHub.Domain.Jobs;
using ErpSyncHub.IntegrationTests.Support;

namespace ErpSyncHub.IntegrationTests.Admin;

/// <summary>El panel de jobs: listado con filtros y paginación, y detalle con intentos y errores.</summary>
[Collection(PostgresCollectionDefinition.Name)]
public class JobPanelTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    private Task<WebhookHost> StartAsync() => WebhookHost.StartAsync(postgres, seed: SeedAsync);

    /// <summary>Seis jobs en distintos estados, creados con una hora de diferencia: 1001 el más antiguo y 1006 el más nuevo.</summary>
    private static async Task SeedAsync(string connectionString)
    {
        await using var context = PostgresFixture.CreateContext(connectionString);

        // 1001: terminó bien.
        var succeeded = NewJob("1001", SyncJobTypes.Order, T0);
        var attempt = succeeded.Claim("worker-1", T0, Lease).Value;
        succeeded.MarkSucceeded("worker-1", new SapOrderReference(7, 1007, "C18765432-7", 24990m), T0.AddSeconds(2));
        attempt.Succeed(T0.AddSeconds(2));
        context.AddRange(succeeded, attempt);

        // 1002: error definitivo.
        var failed = NewJob("1002", SyncJobTypes.Order, T0.AddHours(1));
        var failedAttempt = failed.Claim("worker-1", T0.AddHours(1), Lease).Value;
        var unknownSku = ResultError.Rejected("order.unknown_sku", "El SKU 'X' no tiene mapeo ni existe en SAP.");
        failed.MarkFailed("worker-1", unknownSku, T0.AddHours(1).AddSeconds(1));
        failedAttempt.Fail(unknownSku, T0.AddHours(1).AddSeconds(1));
        context.AddRange(failed, failedAttempt);

        // 1003: dos intentos transitorios y a la espera del tercero.
        var retrying = NewJob("1003", SyncJobTypes.Order, T0.AddHours(2));
        var transient = ResultError.Transient("sap.unreachable", "No se pudo conectar con SAP.");
        var first = retrying.Claim("worker-1", T0.AddHours(2), Lease).Value;
        retrying.ScheduleRetry("worker-1", transient, T0.AddHours(2).AddSeconds(30), T0.AddHours(2).AddSeconds(1));
        first.Fail(transient, T0.AddHours(2).AddSeconds(1));
        var second = retrying.Claim("worker-2", T0.AddHours(2).AddSeconds(30), Lease).Value;
        retrying.ScheduleRetry("worker-2", transient, T0.AddHours(2).AddMinutes(2), T0.AddHours(2).AddSeconds(31));
        second.Fail(transient, T0.AddHours(2).AddSeconds(31));
        context.AddRange(retrying, first, second);

        // 1004: agotó los intentos.
        var dead = NewJob("1004", SyncJobTypes.Order, T0.AddHours(3));
        var deadAttempt = dead.Claim("worker-1", T0.AddHours(3), Lease).Value;
        dead.MarkDeadLetter("worker-1", transient, T0.AddHours(3).AddSeconds(1));
        deadAttempt.Fail(transient, T0.AddHours(3).AddSeconds(1));
        context.AddRange(dead, deadAttempt);

        // 1005: recién llegado. 9001: de otro tipo.
        context.Add(NewJob("1005", SyncJobTypes.Order, T0.AddHours(4)));
        context.Add(NewJob("9001", "refund", T0.AddHours(5)));

        await context.SaveChangesAsync();
    }

    private static SyncJob NewJob(string externalId, string type, DateTimeOffset createdAt) =>
        SyncJob.Create(
            SyncSources.WooCommerce,
            externalId,
            type,
            "{\"id\":\"" + externalId + "\",\"billing\":{\"first_name\":\"Valentina\",\"email\":\"valentina@example.com\"}}",
            29990m,
            createdAt).Value;

    private static async Task<JsonDocument> GetJsonAsync(WebhookHost host, string url)
    {
        var response = await host.Client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return await WebhookHost.ReadJsonAsync(response);
    }

    private static string[] ExternalIds(JsonDocument page) =>
        [.. page.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("externalId").GetString()!)];

    [Fact]
    public async Task Lista_los_jobs_del_mas_reciente_al_mas_antiguo_con_el_total()
    {
        await using var host = await StartAsync();

        using var page = await GetJsonAsync(host, "/admin/jobs");

        ExternalIds(page).ShouldBe(["9001", "1005", "1004", "1003", "1002", "1001"]);
        page.RootElement.GetProperty("totalItems").GetInt32().ShouldBe(6);
        page.RootElement.GetProperty("totalPages").GetInt32().ShouldBe(1);
        page.RootElement.GetProperty("page").GetInt32().ShouldBe(1);
        page.RootElement.GetProperty("pageSize").GetInt32().ShouldBe(20);
    }

    [Fact]
    public async Task El_listado_expone_estado_intentos_error_y_orden_de_SAP_sin_el_payload()
    {
        await using var host = await StartAsync();

        using var page = await GetJsonAsync(host, "/admin/jobs?externalId=1003");
        var job = page.RootElement.GetProperty("items")[0];

        job.GetProperty("status").GetString().ShouldBe("retrying");
        job.GetProperty("attemptCount").GetInt32().ShouldBe(2);
        job.GetProperty("cycleAttempts").GetInt32().ShouldBe(2);
        job.GetProperty("nextAttemptAt").GetDateTimeOffset().ShouldBe(T0.AddHours(2).AddMinutes(2));
        job.GetProperty("lastError").GetProperty("code").GetString().ShouldBe("sap.unreachable");
        job.GetProperty("sap").ValueKind.ShouldBe(JsonValueKind.Null);
        job.TryGetProperty("payload", out _).ShouldBeFalse();
        page.RootElement.GetRawText().ShouldNotContain("valentina@example.com");

        using var ok = await GetJsonAsync(host, "/admin/jobs?externalId=1001");
        var done = ok.RootElement.GetProperty("items")[0];
        done.GetProperty("status").GetString().ShouldBe("succeeded");
        done.GetProperty("sap").GetProperty("docNum").GetInt32().ShouldBe(1007);
        done.GetProperty("lastError").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Theory]
    [InlineData("status=failed", new[] { "1002" })]
    [InlineData("status=dead_letter", new[] { "1004" })]
    [InlineData("status=retrying", new[] { "1003" })]
    [InlineData("status=pending", new[] { "9001", "1005" })]
    [InlineData("status=succeeded", new[] { "1001" })]
    [InlineData("status=failed&status=dead_letter", new[] { "1004", "1002" })]
    [InlineData("status=failed,dead_letter,retrying", new[] { "1004", "1003", "1002" })]
    [InlineData("status=Failed", new[] { "1002" })]
    [InlineData("status=pending&type=order", new[] { "1005" })]
    [InlineData("type=refund", new[] { "9001" })]
    [InlineData("type=nada", new string[0])]
    [InlineData("externalId=1004", new[] { "1004" })]
    [InlineData("externalId=100", new string[0])]
    public async Task Filtra_por_estado_tipo_e_id_externo(string query, string[] expected)
    {
        await using var host = await StartAsync();

        using var page = await GetJsonAsync(host, $"/admin/jobs?{query}");

        ExternalIds(page).ShouldBe(expected);
        page.RootElement.GetProperty("totalItems").GetInt32().ShouldBe(expected.Length);
    }

    [Fact]
    public async Task Filtra_por_fecha_de_creacion_con_from_inclusivo_y_to_exclusivo()
    {
        await using var host = await StartAsync();
        var from = Uri.EscapeDataString(T0.AddHours(1).ToString("O"));
        var to = Uri.EscapeDataString(T0.AddHours(4).ToString("O"));

        using var page = await GetJsonAsync(host, $"/admin/jobs?from={from}&to={to}");

        ExternalIds(page).ShouldBe(["1004", "1003", "1002"]);
    }

    [Fact]
    public async Task Una_fecha_sin_zona_se_interpreta_como_UTC_y_solo_la_fecha_vale_como_medianoche()
    {
        await using var host = await StartAsync();

        using var fromNoon = await GetJsonAsync(host, "/admin/jobs?from=2026-03-10T15:00:00");
        using var wholeDay = await GetJsonAsync(host, "/admin/jobs?from=2026-03-10&to=2026-03-11");
        using var nothing = await GetJsonAsync(host, "/admin/jobs?from=2026-03-11");

        ExternalIds(fromNoon).ShouldBe(["9001", "1005", "1004"]);
        wholeDay.RootElement.GetProperty("totalItems").GetInt32().ShouldBe(6);
        ExternalIds(nothing).ShouldBeEmpty();
    }

    [Fact]
    public async Task Pagina_los_resultados_sin_repetir_ni_saltar_jobs()
    {
        await using var host = await StartAsync();

        using var first = await GetJsonAsync(host, "/admin/jobs?pageSize=4&page=1");
        using var second = await GetJsonAsync(host, "/admin/jobs?pageSize=4&page=2");
        using var beyond = await GetJsonAsync(host, "/admin/jobs?pageSize=4&page=3");

        ExternalIds(first).ShouldBe(["9001", "1005", "1004", "1003"]);
        ExternalIds(second).ShouldBe(["1002", "1001"]);
        ExternalIds(beyond).ShouldBeEmpty();
        first.RootElement.GetProperty("totalPages").GetInt32().ShouldBe(2);
        beyond.RootElement.GetProperty("totalItems").GetInt32().ShouldBe(6);
    }

    [Theory]
    [InlineData("status=inventado", "inventado")]
    [InlineData("status=failed,otro", "otro")]
    [InlineData("page=0", "page")]
    [InlineData("page=-1", "page")]
    [InlineData("pageSize=0", "pageSize")]
    [InlineData("pageSize=101", "pageSize")]
    [InlineData("from=ayer", "from")]
    [InlineData("to=mañana", "to")]
    [InlineData("from=2026-03-11&to=2026-03-10", "from debe ser anterior a to")]
    [InlineData("from=2026-03-10&to=2026-03-10", "from debe ser anterior a to")]
    public async Task Un_parametro_invalido_responde_400_con_ProblemDetails_que_dice_cual(string query, string expected)
    {
        await using var host = await StartAsync();

        var response = await host.Client.GetAsync($"/admin/jobs?{query}");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        (await response.Content.ReadAsStringAsync()).ShouldContain(expected);
    }

    [Fact]
    public async Task Un_estado_invalido_lista_los_estados_validos()
    {
        await using var host = await StartAsync();

        var body = await (await host.Client.GetAsync("/admin/jobs?status=inventado")).Content.ReadAsStringAsync();

        body.ShouldContain("pending, processing, retrying, succeeded, failed, dead_letter");
    }

    [Fact]
    public async Task El_detalle_trae_todos_los_intentos_en_orden_con_sus_errores()
    {
        await using var host = await StartAsync();
        using var list = await GetJsonAsync(host, "/admin/jobs?externalId=1003");
        var id = list.RootElement.GetProperty("items")[0].GetProperty("id").GetGuid();

        using var detail = await GetJsonAsync(host, $"/admin/jobs/{id}");

        detail.RootElement.GetProperty("job").GetProperty("status").GetString().ShouldBe("retrying");
        var attempts = detail.RootElement.GetProperty("attempts").EnumerateArray().ToList();
        attempts.Select(attempt => attempt.GetProperty("number").GetInt32()).ShouldBe([1, 2]);
        attempts.Select(attempt => attempt.GetProperty("workerId").GetString()).ShouldBe(["worker-1", "worker-2"]);
        attempts.ShouldAllBe(attempt => attempt.GetProperty("outcome").GetString() == "failed");
        attempts[0].GetProperty("errorKind").GetString().ShouldBe("transient");
        attempts[0].GetProperty("error").GetProperty("code").GetString().ShouldBe("sap.unreachable");
        attempts[0].GetProperty("durationSeconds").GetDouble().ShouldBe(1);
        detail.RootElement.GetProperty("payload").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task El_detalle_de_un_job_exitoso_muestra_el_intento_correcto_y_la_orden_de_SAP()
    {
        await using var host = await StartAsync();
        using var list = await GetJsonAsync(host, "/admin/jobs?externalId=1001");
        var id = list.RootElement.GetProperty("items")[0].GetProperty("id").GetGuid();

        using var detail = await GetJsonAsync(host, $"/admin/jobs/{id}");

        var attempt = detail.RootElement.GetProperty("attempts").EnumerateArray().Single();
        attempt.GetProperty("outcome").GetString().ShouldBe("succeeded");
        attempt.GetProperty("error").ValueKind.ShouldBe(JsonValueKind.Null);
        attempt.GetProperty("errorKind").ValueKind.ShouldBe(JsonValueKind.Null);
        detail.RootElement.GetProperty("job").GetProperty("sap").GetProperty("docEntry").GetInt32().ShouldBe(7);
    }

    [Fact]
    public async Task El_payload_con_datos_personales_solo_se_devuelve_si_se_pide_y_como_JSON()
    {
        await using var host = await StartAsync();
        using var list = await GetJsonAsync(host, "/admin/jobs?externalId=1002");
        var id = list.RootElement.GetProperty("items")[0].GetProperty("id").GetGuid();

        using var without = await GetJsonAsync(host, $"/admin/jobs/{id}");
        using var explicitFalse = await GetJsonAsync(host, $"/admin/jobs/{id}?includePayload=false");
        using var with = await GetJsonAsync(host, $"/admin/jobs/{id}?includePayload=true");

        without.RootElement.GetRawText().ShouldNotContain("valentina@example.com");
        explicitFalse.RootElement.GetRawText().ShouldNotContain("Valentina");
        var payload = with.RootElement.GetProperty("payload");
        payload.ValueKind.ShouldBe(JsonValueKind.Object);
        payload.GetProperty("billing").GetProperty("email").GetString().ShouldBe("valentina@example.com");
    }

    [Fact]
    public async Task Un_job_inexistente_responde_404_y_un_id_mal_formado_tambien()
    {
        await using var host = await StartAsync();

        var missing = await host.Client.GetAsync($"/admin/jobs/{Guid.NewGuid()}");
        var malformed = await host.Client.GetAsync("/admin/jobs/no-es-un-guid");

        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        missing.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        malformed.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Sin_la_clave_del_panel_el_listado_y_el_detalle_responden_401()
    {
        await using var host = await StartAsync();
        using var anonymous = host.CreateAnonymousClient();

        (await anonymous.GetAsync("/admin/jobs")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.GetAsync($"/admin/jobs/{Guid.NewGuid()}?includePayload=true")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Sin_jobs_el_listado_es_una_pagina_vacia()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        await using var factory = ApiFactory.Create(connectionString);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiFactory.AdminApiKey);

        using var page = await WebhookHost.ReadJsonAsync(await client.GetAsync("/admin/jobs"));

        page.RootElement.GetProperty("items").GetArrayLength().ShouldBe(0);
        page.RootElement.GetProperty("totalItems").GetInt32().ShouldBe(0);
        page.RootElement.GetProperty("totalPages").GetInt32().ShouldBe(0);
    }
}
