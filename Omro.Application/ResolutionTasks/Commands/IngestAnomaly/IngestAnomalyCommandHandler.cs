using MediatR;
using Omro.Domain.Repositories;
using Omro.Domain.ResolutionTasks;

namespace Omro.Application.ResolutionTasks.Commands.IngestAnomaly;

public class IngestAnomalyCommandHandler : IRequestHandler<IngestAnomalyCommand, Guid>
{
    private readonly IResolutionTaskRepository _repository;
    private readonly IUnitOfWork _unitOfWork;

    public IngestAnomalyCommandHandler(
        IResolutionTaskRepository repository, 
        IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
    }
    
    public async Task<Guid> Handle(IngestAnomalyCommand request, CancellationToken cancellationToken)
    {
        var anomalyEvent = new AnomalyEvent(
            RiskProbability: request.RiskProbability,
            ResourceId: request.ResourceId);
        
        var resolutionTask = new ResolutionTask(request.TenantId, anomalyEvent);

        _repository.Add(resolutionTask);
        
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        
        return resolutionTask.Id;
    }
}