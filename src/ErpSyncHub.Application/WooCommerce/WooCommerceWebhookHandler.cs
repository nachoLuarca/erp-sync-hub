using System.Text;
using ErpSyncHub.Application.Jobs;
using ErpSyncHub.Domain.Common;
using ErpSyncHub.Domain.Jobs;

namespace ErpSyncHub.Application.WooCommerce;

/// <summary>Qué pasó con una entrega del webhook.</summary>
public enum WebhookOutcome
{
    /// <summary>Era el ping de creación del webhook; no hay nada que guardar.</summary>
    Pong,

    /// <summary>El pedido quedó guardado para sincronizarlo.</summary>
    Accepted,

    /// <summary>El pedido ya estaba guardado de una entrega anterior; no se vuelve a procesar.</summary>
    Duplicate,

    /// <summary>El topic no trae un pedido completo (por ejemplo, <c>order.deleted</c>).</summary>
    IgnoredTopic,

    /// <summary>El pedido no está en un estado que se sincronice (por ejemplo, aún no se paga).</summary>
    IgnoredStatus,
}

/// <param name="Outcome">Qué pasó con la entrega.</param>
/// <param name="JobId">El job guardado, para <see cref="WebhookOutcome.Accepted"/> y <see cref="WebhookOutcome.Duplicate"/>.</param>
/// <param name="Detail">Motivo, para los desenlaces "ignorado".</param>
public sealed record WebhookReceipt(WebhookOutcome Outcome, Guid? JobId = null, string? Detail = null);

/// <summary>Códigos de error de la recepción del webhook.</summary>
public static class WebhookErrors
{
    /// <summary>La firma no corresponde al cuerpo recibido.</summary>
    public const string InvalidSignature = "webhook.invalid_signature";
}

/// <summary>
/// Recibe los webhooks de pedidos de WooCommerce (patrón inbox): verifica la firma, filtra lo que no corresponde y
/// guarda el pedido como job para que el worker lo sincronice después. No habla con SAP: responder rápido a la tienda
/// importa más que procesar en el acto.
/// </summary>
public sealed class WooCommerceWebhookHandler(IJobQueue queue, TimeProvider time, WooCommerceWebhookOptions options)
{
    private readonly IReadOnlySet<string> _acceptedStatuses = options.EffectiveAcceptedStatuses();

    /// <param name="body">Cuerpo exactamente como llegó; la firma se calcula sobre estos bytes.</param>
    /// <param name="signature">Valor de la cabecera <c>X-WC-Webhook-Signature</c>.</param>
    /// <param name="topic">Valor de la cabecera <c>X-WC-Webhook-Topic</c>, si vino.</param>
    /// <param name="cancellationToken">Cancelación de la solicitud.</param>
    public async Task<Result<WebhookReceipt>> HandleAsync(
        ReadOnlyMemory<byte> body,
        string? signature,
        string? topic,
        CancellationToken cancellationToken)
    {
        // El ping de creación no transporta datos: responderlo no expone nada y evita que WooCommerce marque
        // el webhook como fallido si el ping no estuviera firmado.
        if (WooCommercePing.IsPing(body.Span))
        {
            return Result.Success(new WebhookReceipt(WebhookOutcome.Pong));
        }

        // La firma se verifica antes de interpretar el cuerpo: un cuerpo hostil nunca llega al lector de JSON.
        if (!WooCommerceSignature.IsValid(options.Secret, body.Span, signature))
        {
            return Result.Failure<WebhookReceipt>(ResultError.Validation(
                WebhookErrors.InvalidSignature,
                "La firma de la solicitud no coincide con el cuerpo recibido."));
        }

        // Los topics de borrado o restauración traen un cuerpo sin los datos del pedido: se ignoran con un 200,
        // porque un error haría que WooCommerce reintente y termine desactivando el webhook.
        if (topic is not null && !WooCommerceWebhookOptions.OrderTopics.Contains(topic.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            return Result.Success(new WebhookReceipt(WebhookOutcome.IgnoredTopic, Detail: $"topic:{topic.Trim()}"));
        }

        var parsed = WooCommerceOrderParser.Parse(body.Span);
        if (parsed.IsFailure)
        {
            return Result.Failure<WebhookReceipt>(parsed.Error!);
        }

        var order = parsed.Value;
        if (!_acceptedStatuses.Contains(order.Status))
        {
            return Result.Success(new WebhookReceipt(WebhookOutcome.IgnoredStatus, Detail: $"status:{order.Status}"));
        }

        var job = SyncJob.Create(
            SyncSources.WooCommerce,
            order.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            SyncJobTypes.Order,
            Encoding.UTF8.GetString(body.Span),
            order.Total,
            time.GetUtcNow());

        if (job.IsFailure)
        {
            return Result.Failure<WebhookReceipt>(job.Error!);
        }

        // El índice único (origen, id externo, tipo) hace idempotente la recepción: la tienda reenvía el mismo pedido
        // al actualizarlo (processing → completed) o si no recibió nuestra respuesta, y solo se procesa una vez.
        var enqueued = await queue.EnqueueAsync(job.Value, cancellationToken);
        return Result.Success(new WebhookReceipt(
            enqueued.Created ? WebhookOutcome.Accepted : WebhookOutcome.Duplicate,
            enqueued.JobId));
    }
}
