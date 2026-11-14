using MediatR;
using Omro.Application.Interfaces;
using Omro.Domain.Repositories;
using Omro.Domain.ResolutionTasks;

namespace Omro.Application.ResolutionTasks.Commands.IngestAnomaly;

public class IngestAnomalyCommandHandler : IRequestHandler<IngestAnomalyCommand, Guid>
{
    private readonly IResolutionTaskRepository _repository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICognitiveOrchestrator _orchestrator;

    public IngestAnomalyCommandHandler(
        IResolutionTaskRepository repository, 
        IUnitOfWork unitOfWork,
        ICognitiveOrchestrator orchestrator)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
        _orchestrator = orchestrator;
    }
    
    public async Task<Guid> Handle(IngestAnomalyCommand request, CancellationToken cancellationToken)
    {
        // rehidratacion 
        var anomalyEvent = new AnomalyEvent(
            RiskProbability: request.RiskProbability,
            ResourceId: request.ResourceId);
        
        var resolutionTask = new ResolutionTask(request.TenantId, anomalyEvent);
        
        // mutación de dominio
        resolutionTask.StartAnalysis();

        // reglas de negocio (a futuro buscar en db por tenantId)
        string tenantRules =
            "Priorizar acciones conservadoras. Si la probabilidad de riesgo supera el 0.8, propón un WEBHOOK de alerta inmediata.";

        // razonamiento cognitivo: el modelo asume el control
        var (plan, promptTokens, completionTokens) = await _orchestrator.ProposeResolutionAsync(
            anomalyEvent,
            tenantRules,
            cancellationToken);
        
        // mutacion de dominio final: empaquetamos el JSON y los costos
        resolutionTask.ProposePlan(plan, promptTokens, completionTokens);
        
        // persistencia
        _repository.Add(resolutionTask);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        
        return resolutionTask.Id;
    }
}