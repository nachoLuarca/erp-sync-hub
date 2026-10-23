using ErpSyncHub.Domain.Jobs;
using ErpSyncHub.Domain.Mappings;
using Microsoft.EntityFrameworkCore;

namespace ErpSyncHub.Infrastructure.Persistence;

/// <summary>Base de datos del hub en PostgreSQL: cola de jobs, historial de intentos y mapeos.</summary>
public sealed class ErpSyncHubDbContext(DbContextOptions<ErpSyncHubDbContext> options) : DbContext(options)
{
    public DbSet<SyncJob> SyncJobs => Set<SyncJob>();

    public DbSet<SyncAttempt> SyncAttempts => Set<SyncAttempt>();

    public DbSet<ProductMapping> ProductMappings => Set<ProductMapping>();

    public DbSet<CustomerMapping> CustomerMappings => Set<CustomerMapping>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ErpSyncHubDbContext).Assembly);
    }
}
