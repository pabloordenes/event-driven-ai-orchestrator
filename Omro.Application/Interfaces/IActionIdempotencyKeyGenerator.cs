using Omro.Domain.ResolutionTasks;

namespace Omro.Application.Interfaces;

public interface IActionIdempotencyKeyGenerator
{
    string Generate(
        Guid taskId,
        ActionExecutionPhase phase,
        int actionIndex,
        string actionType,
        string httpMethod,
        string endpoint);
}
