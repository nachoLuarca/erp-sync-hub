namespace SapB1.Mock.Faults;

/// <summary>Administración de las fallas simuladas, fuera del contrato del Service Layer y sin sesión.</summary>
internal static class FaultEndpoints
{
    public static void MapFaults(this RouteGroupBuilder admin)
    {
        admin.MapGet("/faults", (IFaultInjector faults) => Results.Ok(faults.Report()));

        admin.MapPut("/faults", (FaultSettings settings, IFaultInjector faults) =>
        {
            if (settings.Validate() is { } problem)
            {
                return Results.BadRequest(new { error = problem });
            }

            faults.Set(settings);
            return Results.Ok(faults.Report());
        });

        admin.MapDelete("/faults", (IFaultInjector faults) =>
        {
            faults.Clear();
            return Results.NoContent();
        });
    }
}
