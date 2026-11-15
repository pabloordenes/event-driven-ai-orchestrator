namespace Omro.Domain.ResolutionTasks;

public record ResolutionApprovedEvent(Guid ResolutionTaskId, string AiPlanJson);