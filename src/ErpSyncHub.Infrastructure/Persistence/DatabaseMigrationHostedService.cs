using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ErpSyncHub.Infrastructure.Persistence;

/// <summary>Aplica las migraciones pendientes antes de que arranque el resto de la aplicación, si está habilitado.</summary>
internal sealed partial class DatabaseMigrationHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<DatabaseOptions> options,
    ILogger<DatabaseMigrationHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.MigrateOnStartup)
        {
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ErpSyncHubDbContext>();

        var pending = (await context.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        LogMigrating(pending.Count);

        await context.Database.MigrateAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information, Message = "Aplicando {PendingMigrations} migraciones pendientes de la base de datos")]
    private partial void LogMigrating(int pendingMigrations);
}
