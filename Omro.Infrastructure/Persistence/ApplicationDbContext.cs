using Microsoft.EntityFrameworkCore;
using Omro.Application.Interfaces;
using Omro.Domain.Cognitive;
using Omro.Domain.Messaging;
using Omro.Domain.ResolutionTasks;
using Omro.Domain.Tenants;

namespace Omro.Infrastructure.Persistence;

public class ApplicationDbContext : DbContext, IApplicationDbContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options) { }
    
    public DbSet<ResolutionTask> ResolutionTasks => Set<ResolutionTask>();
    public DbSet<ActionExecution> ActionExecutions => Set<ActionExecution>();
    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();
    public DbSet<TenantConfig> TenantConfigs => Set<TenantConfig>();
    public DbSet<TenantAction> TenantActions => Set<TenantAction>();
    public DbSet<TenantPolicy> TenantPolicies => Set<TenantPolicy>();
    public DbSet<ShadowDecision> ShadowDecisions => Set<ShadowDecision>();
    public DbSet<LlmUsageLog> LlmUsageLogs => Set<LlmUsageLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        
        // aislamiento logico
        modelBuilder.HasDefaultSchema("omro");
        
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ApplicationDbContext).Assembly);
    }
}
