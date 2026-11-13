using Microsoft.EntityFrameworkCore;
using Omro.Application.Interfaces;
using Omro.Domain.ResolutionTasks;

namespace Omro.Infrastructure.Persistence;

public class ApplicationDbContext : DbContext, IApplicationDbContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options) { }
    
    public DbSet<ResolutionTask> ResolutionTasks => Set<ResolutionTask>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        
        // aislamiento logico
        modelBuilder.HasDefaultSchema("omro");
        
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ApplicationDbContext).Assembly);
    }
}