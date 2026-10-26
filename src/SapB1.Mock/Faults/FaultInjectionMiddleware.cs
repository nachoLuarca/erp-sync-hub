using SapB1.Mock.ServiceLayer;

namespace SapB1.Mock.Faults;

/// <summary>
/// Inyecta las fallas activas en las peticiones del Service Layer (<c>/b1s/v1</c>): primero la latencia, luego la caída total
/// y por último los errores intermitentes. No toca <c>/health</c> ni <c>/_mock</c>.
/// </summary>
internal sealed class FaultInjectionMiddleware(RequestDelegate next, IFaultInjector faults, TimeProvider time)
{
    private static readonly PathString ServiceLayerPath = new("/b1s/v1");

    public async Task InvokeAsync(HttpContext http)
    {
        var settings = faults.Current;
        if (settings.IsEmpty || !http.Request.Path.StartsWithSegments(ServiceLayerPath))
        {
            await next(http);
            return;
        }

        if (settings.LatencyMs > 0)
        {
            faults.RecordDelay();
            await Task.Delay(TimeSpan.FromMilliseconds(settings.LatencyMs), time, http.RequestAborted);
        }

        if (settings.Outage)
        {
            faults.RecordOutage();
            if (settings.OutageMode == OutageMode.ConnectionReset)
            {
                http.Abort();
                return;
            }

            await SapResults.Error(StatusCodes.Status503ServiceUnavailable, SapErrorCodes.Unavailable, "Service Layer is unavailable.")
                .ExecuteAsync(http);
            return;
        }

        if (faults.ShouldFailRequest(out var statusCode))
        {
            await SapResults.Error(statusCode, SapErrorCodes.InternalError, "Internal server error (simulated fault).")
                .ExecuteAsync(http);
            return;
        }

        await next(http);
    }
}
