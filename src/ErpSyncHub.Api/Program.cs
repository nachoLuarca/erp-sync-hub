using ErpSyncHub.Api.Health;
using ErpSyncHub.Api.Problems;
using ErpSyncHub.Api.Sync;
using ErpSyncHub.Api.Webhooks;
using ErpSyncHub.Application;
using ErpSyncHub.Infrastructure;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Scalar.AspNetCore;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

var builder = WebApplication.CreateBuilder(args);

// Logs estructurados: JSON compacto en producción, texto legible en desarrollo. Los niveles vienen de la
// configuración (sección Serilog) y los ámbitos de ILogger, como el del job, se vuelven propiedades de cada línea.
builder.Host.UseSerilog((context, services, logger) =>
{
    logger
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .Enrich.WithProperty("Application", "ErpSyncHub");

    // Seq es opcional: sin Seq:ServerUrl el servicio funciona igual, solo con la consola.
    var seqUrl = context.Configuration["Seq:ServerUrl"];
    if (!string.IsNullOrWhiteSpace(seqUrl))
    {
        if (!Uri.TryCreate(seqUrl, UriKind.Absolute, out var seqUri) || seqUri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException(
                $"Seq:ServerUrl (variable Seq__ServerUrl) debe ser una URL http(s) absoluta, por ejemplo http://seq:80. Valor recibido: '{seqUrl}'.");
        }

        // El sink envía en segundo plano y por lotes: si Seq está caído o lento, el hub no se detiene ni pierde el log de consola.
        logger.WriteTo.Seq(seqUri.ToString(), apiKey: context.Configuration["Seq:ApiKey"]);
    }

    if (context.HostingEnvironment.IsDevelopment())
    {
        logger.WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}");
    }
    else
    {
        logger.WriteTo.Console(new RenderedCompactJsonFormatter());
    }
});

builder.Services.AddOpenApi();
builder.Services.AddHealthChecks();
builder.Services.AddApiProblemDetails();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddApplication();
builder.Services.AddWooCommerceWebhooks(builder.Configuration);
builder.Services.AddOrderSync(builder.Configuration);

var app = builder.Build();

app.UseExceptionHandler();
app.UseSerilogRequestLogging(options =>
{
    // Las sondas de /health son constantes: no deben llenar el registro.
    options.GetLevel = (httpContext, _, exception) =>
        exception is not null || httpContext.Response.StatusCode >= StatusCodes.Status500InternalServerError
            ? LogEventLevel.Error
            : httpContext.Request.Path.StartsWithSegments("/health", StringComparison.Ordinal)
                ? LogEventLevel.Verbose
                : LogEventLevel.Information;

    options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
    {
        var delivery = httpContext.Request.Headers["X-WC-Webhook-Delivery-ID"].ToString();
        if (delivery.Length > 0)
        {
            diagnosticContext.Set("DeliveryId", delivery);
        }
    };
});
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

// PostgreSQL caído responde 503; SAP caído solo degrada (200): el hub sigue recibiendo pedidos.
app.MapHealthChecks("/health", new HealthCheckOptions
{
    ResponseWriter = HealthResponseWriter.WriteAsync,
    ResultStatusCodes =
    {
        [HealthStatus.Healthy] = StatusCodes.Status200OK,
        [HealthStatus.Degraded] = StatusCodes.Status200OK,
        [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable,
    },
});
app.MapWooCommerceWebhooks();

app.Run();

public partial class Program;
