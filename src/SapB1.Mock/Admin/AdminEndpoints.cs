using SapB1.Mock.Items;
using SapB1.Mock.Sessions;

namespace SapB1.Mock.Admin;

/// <summary>
/// Endpoints propios del simulador, fuera del contrato del Service Layer y sin sesión,
/// pensados para pruebas automáticas y para simular fallas.
/// </summary>
internal static class AdminEndpoints
{
    public static void MapAdmin(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/_mock").ExcludeFromDescription();

        admin.MapPost("/reset", (IEnumerable<IResettable> resettables) =>
        {
            foreach (var resettable in resettables)
            {
                resettable.Reset();
            }

            return Results.NoContent();
        });

        admin.MapPost("/sessions/expire", (ISessionStore sessions) =>
        {
            sessions.Clear();
            return Results.NoContent();
        });

        admin.MapItemAdmin();
    }
}
