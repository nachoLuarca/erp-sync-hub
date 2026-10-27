using SapB1.Mock.Faults;
using SapB1.Mock.Sessions;

namespace SapB1.Mock.ServiceLayer;

/// <summary>Exige una sesión vigente (cookie <c>B1SESSION</c>) y renueva su plazo de inactividad.</summary>
internal sealed class SessionRequiredFilter(ISessionStore sessions, IFaultInjector faults) : IEndpointFilter
{
    public const string CookieName = "B1SESSION";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var hasSession = context.HttpContext.Request.Cookies.TryGetValue(CookieName, out var sessionId);

        if (!hasSession || string.IsNullOrEmpty(sessionId) || !sessions.TryTouch(sessionId))
        {
            return SapResults.InvalidSession();
        }

        // Expiración forzada (fallas simuladas): la sesión vigente aparece expirada y se pierde, como si SAP la hubiera cerrado.
        if (faults.ShouldExpireSession())
        {
            sessions.Remove(sessionId);
            return SapResults.InvalidSession();
        }

        return await next(context);
    }
}
