using ErpSyncHub.Application.Resilience;
using ErpSyncHub.Domain.Resilience;
using ErpSyncHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ErpSyncHub.Infrastructure.Resilience;

/// <summary>
/// Los circuitos en la tabla <c>circuit_breakers</c>. Cada cambio se hace en una transacción corta que bloquea la fila
/// (<c>FOR UPDATE</c>): si dos instancias del hub deciden a la vez, la segunda espera y ve el resultado de la primera. Así hay una sola
/// prueba semiabierta en toda la flota y el contador de fallas no se pisa.
/// </summary>
internal sealed class CircuitBreakerStore(ErpSyncHubDbContext context, TimeProvider time) : ICircuitBreakerStore
{
    public async Task<CircuitSnapshot> GetAsync(string name, CancellationToken cancellationToken)
    {
        var circuit = await context.CircuitBreakers
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Name == name, cancellationToken);

        return CircuitSnapshot.From(circuit ?? CircuitBreaker.Create(name, time.GetUtcNow()));
    }

    public async Task<IReadOnlyList<CircuitSnapshot>> ListAsync(CancellationToken cancellationToken)
    {
        var circuits = await context.CircuitBreakers.AsNoTracking().OrderBy(circuit => circuit.Name).ToListAsync(cancellationToken);
        return [.. circuits.Select(CircuitSnapshot.From)];
    }

    public async Task<TResult> UpdateAsync<TResult>(string name, Func<CircuitBreaker, TResult> change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        // La fila se crea cerrada la primera vez; si dos instancias la crean a la vez, una de las dos no hace nada.
        var now = time.GetUtcNow();
        await context.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO circuit_breakers (name, state, consecutive_failures, open_count, updated_at)
            VALUES ({name}, 'closed', 0, 0, {now})
            ON CONFLICT (name) DO NOTHING
            """,
            cancellationToken);

        var circuit = await context.CircuitBreakers
            .FromSql($"SELECT * FROM circuit_breakers WHERE name = {name} FOR UPDATE")
            .AsNoTracking()
            .SingleAsync(cancellationToken);

        var result = change(circuit);

        context.CircuitBreakers.Update(circuit);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        // La próxima lectura de este contexto debe ver la fila de la base de datos, no esta copia.
        context.Entry(circuit).State = EntityState.Detached;
        return result;
    }
}
