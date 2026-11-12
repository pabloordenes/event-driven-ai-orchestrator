using Omro.Domain.Repositories;
using Omro.Domain.ResolutionTasks;

namespace Omro.Infrastructure.Persistence.Repositories;

public class ResolutionTaskRepository : IResolutionTaskRepository
{
    private readonly ApplicationDbContext _dbContext;
    
    public ResolutionTaskRepository(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public void Add(ResolutionTask task)
    {
        _dbContext.ResolutionTasks.AddAsync(task);
    }
}