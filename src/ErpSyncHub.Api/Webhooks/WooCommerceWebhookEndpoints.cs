using ErpSyncHub.Api.Problems;
using ErpSyncHub.Application.WooCommerce;
using ErpSyncHub.Domain.Common;
using Microsoft.Extensions.Options;

namespace ErpSyncHub.Api.Webhooks;

internal static partial class WooCommerceWebhookEndpoints
{
    private const string TopicHeader = "X-WC-Webhook-Topic";
    private const string DeliveryHeader = "X-WC-Webhook-Delivery-ID";

    public static void MapWooCommerceWebhooks(this IEndpointRouteBuilder app)
    {
        app.MapPost("/webhooks/woocommerce/orders", ReceiveOrderAsync)
            .WithName("ReceiveWooCommerceOrder")
            .WithSummary("Recibe los webhooks de pedidos de WooCommerce.")
            .WithDescription(
                "Verifica la firma X-WC-Webhook-Signature (HMAC-SHA256 en Base64 sobre el cuerpo crudo) y guarda el pedido " +
                "para sincronizarlo con SAP. Responde 202 al guardar, 200 al ping o a lo que se ignora, y 401 si la firma no coincide.")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge);
    }

    private static async Task<IResult> ReceiveOrderAsync(
        HttpRequest request,
        WooCommerceWebhookHandler handler,
        IOptions<WooCommerceWebhookOptions> options,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("ErpSyncHub.Api.Webhooks.WooCommerce");
        var delivery = request.Headers[DeliveryHeader].ToString();

        var body = await BoundedBody.ReadAsync(request, options.Value.MaxBodyBytes, cancellationToken);
        if (body is null)
        {
            LogBodyTooLarge(logger, delivery, options.Value.MaxBodyBytes);
            return ProblemDetailsSetup.Problem(
                StatusCodes.Status413PayloadTooLarge,
                $"El cuerpo supera el máximo permitido de {options.Value.MaxBodyBytes} bytes.");
        }

        var topic = request.Headers[TopicHeader].ToString();
        var result = await handler.HandleAsync(
            body,
            request.Headers[WooCommerceSignature.HeaderName].ToString(),
            topic.Length > 0 ? topic : null,
            cancellationToken);

        if (result.IsFailure)
        {
            return Reject(logger, delivery, result.Error!);
        }

        var receipt = result.Value;
        LogReceived(logger, delivery, receipt.Outcome, receipt.JobId);

        return receipt.Outcome switch
        {
            WebhookOutcome.Pong => Results.Ok(new { status = "pong" }),
            WebhookOutcome.Accepted => Results.Accepted(value: new { status = "accepted", jobId = receipt.JobId }),
            WebhookOutcome.Duplicate => Results.Accepted(value: new { status = "duplicate", jobId = receipt.JobId }),
            _ => Results.Ok(new { status = "ignored", reason = receipt.Detail }),
        };
    }

    private static IResult Reject(ILogger logger, string delivery, ResultError error)
    {
        if (error.Code == WebhookErrors.InvalidSignature)
        {
            LogInvalidSignature(logger, delivery);
            return ProblemDetailsSetup.Problem(StatusCodes.Status401Unauthorized, error.Message);
        }

        LogInvalidPayload(logger, delivery, error.Code);
        return ProblemDetailsSetup.Problem(StatusCodes.Status400BadRequest, error.Message);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Webhook de WooCommerce rechazado por firma inválida (entrega {DeliveryId})")]
    private static partial void LogInvalidSignature(ILogger logger, string deliveryId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Webhook de WooCommerce rechazado: {ErrorCode} (entrega {DeliveryId})")]
    private static partial void LogInvalidPayload(ILogger logger, string deliveryId, string errorCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Webhook de WooCommerce rechazado: cuerpo mayor a {MaxBytes} bytes (entrega {DeliveryId})")]
    private static partial void LogBodyTooLarge(ILogger logger, string deliveryId, int maxBytes);

    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook de WooCommerce recibido: {Outcome}, job {JobId} (entrega {DeliveryId})")]
    private static partial void LogReceived(ILogger logger, string deliveryId, WebhookOutcome outcome, Guid? jobId);
}
