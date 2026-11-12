using Omro.Domain.ResolutionTasks;

namespace Omro.Domain.Repositories;

public interface IResolutionTaskRepository
{
    void Add(ResolutionTask task);
}