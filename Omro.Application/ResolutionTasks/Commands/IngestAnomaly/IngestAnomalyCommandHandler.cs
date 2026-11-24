using MediatR;
using Microsoft.Extensions.Logging;
using Omro.Application.Interfaces;
using Omro.Application.ResolutionTasks.Services;
using Omro.Domain.Repositories;
using Omro.Domain.ResolutionTasks;

namespace Omro.Application.ResolutionTasks.Commands.IngestAnomaly;

public class IngestAnomalyCommandHandler : IRequestHandler<IngestAnomalyCommand, Guid>
{
    private readonly IResolutionTaskRepository _repository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICognitiveOrchestrator _orchestrator;
    private readonly IActionExecutionCoordinator _coordinator;
    private readonly ILogger<IngestAnomalyCommandHandler> _logger;

    public IngestAnomalyCommandHandler(
        IResolutionTaskRepository repository,
        IUnitOfWork unitOfWork,
        ICognitiveOrchestrator orchestrator,
        IActionExecutionCoordinator coordinator,
        ILogger<IngestAnomalyCommandHandler> logger)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
        _orchestrator = orchestrator;
        _coordinator = coordinator;
        _logger = logger;
    }

    public async Task<Guid> Handle(IngestAnomalyCommand request, CancellationToken cancellationToken)
    {
        var existingTask = await _repository.GetByKeyAsync(
            request.TenantId,
            request.ResourceId,
            request.AnomalyType,
            cancellationToken);

        if (existingTask != null)
        {
            if (existingTask.Status == ResolutionStatus.ActionProposed)
            {
                _logger.LogInformation(
                    "[OMRO RECOVERY] Reanudando tarea existente {TaskId} en estado ActionProposed sin re-invocar al LLM.",
                    existingTask.Id);

                await _coordinator.ExecutePhaseAsync(existingTask, ActionExecutionPhase.Immediate, cancellationToken);
                return existingTask.Id;
            }

            _logger.LogInformation(
                "[OMRO IDEMPOTENCIA] Anomalía duplicada ({TenantId}, {ResourceId}, {AnomalyType}). Retornando tarea existente {TaskId} en estado {Status}.",
                request.TenantId,
                request.ResourceId,
                request.AnomalyType,
                existingTask.Id,
                existingTask.Status);
            return existingTask.Id;
        }

        // rehidratacion 
        var anomalyEvent = new AnomalyEvent(
            RiskProbability: request.RiskProbability,
            ResourceId: request.ResourceId,
            AnomalyType: request.AnomalyType,
            CorrelationId: request.CorrelationId);

        var resolutionTask = new ResolutionTask(request.TenantId, anomalyEvent);

        // mutación de dominio
        resolutionTask.StartAnalysis();

        // Simulamos las reglas de negocio extraídas desde la base de datos para este Tenant (Clínica)
        string tenantPromptContext = """
                                     Eres el sistema de decisiones de MedCareOS para la 'Clínica San José'.
                                     Tu objetivo es mitigar el impacto financiero de los pacientes que no asisten a sus citas (No-Shows).
                                     NOTA: Solo recibes citas que ya fueron catalogadas como Anomalías (Riesgo >= 0.50).

                                     REGLAS DE NEGOCIO:
                                     1. RIESGO CRÍTICO (RiskProbability MAYOR o IGUAL a 0.85, es decir >= 0.85):
                                     - El paciente casi seguro faltará. Autoriza un sobrecupo automático.
                                     - ExpectsExternalApproval: false
                                     - ActionType: "GENERATE_OVERBOOK"
                                     - Endpoint: "https://medcare-cqrs-clinical-api.onrender.com/api/appointments/overbook"
                                     - Payload: Debe incluir "resourceId", "targetRisk": <el valor del riesgo>, y "autoApprove": true.

                                     2. RIESGO MODERADO (RiskProbability MAYOR o IGUAL a 0.50 y MENOR a 0.85, es decir >= 0.50 y < 0.85):
                                     - Riesgo de choque de agenda. NO automatices el sobrecupo.
                                     - Envía una propuesta para que el administrador humano revise el caso (HITL).
                                     - ExpectsExternalApproval: true
                                     - ActionType: "PROPOSE_RESOLUTION"
                                     - Endpoint: "https://medcare-cqrs-clinical-api.onrender.com/api/resolutions/propose"
                                     - Payload: Debe incluir "resourceId", "riskProbability": <el valor del riesgo>, y "aiPlanJson" que contenga un string JSON escapado con la siguiente estructura de Acción Diferida:
                                       { "DeferredActions": [ { "Endpoint": "https://medcare-cqrs-clinical-api.onrender.com/api/appointments/monitor", "Payload": { "resourceId": "<id>", "requiresAction": true } } ] }
                                     """;

        // razonamiento cognitivo: el modelo asume el control
        var (plan, promptTokens, completionTokens) = await _orchestrator.ProposeResolutionAsync(
            anomalyEvent,
            tenantPromptContext,
            cancellationToken);

        _logger.LogInformation(
            "Decisión cognitiva generada para recurso {ResourceId}. Title={Title}, IsFallback={IsFallback}, ExpectsExternalApproval={ExpectsApproval}, Actions={ActionCount}",
            request.ResourceId,
            plan.Title,
            plan.IsFallback,
            plan.ExpectsExternalApproval,
            plan.ProposedActions?.Count ?? 0);

        if (plan.IsFallback)
        {
            resolutionTask.MarkNeedsHumanReview(plan.Reasoning, plan);
        }
        else
        {
            resolutionTask.ProposePlan(plan, promptTokens, completionTokens);
        }

        // Transacción 1: persistir intención (ResolutionTask + ActionExecutions Pending) ANTES de despachar webhooks
        _repository.Add(resolutionTask);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        if (plan.IsFallback)
        {
            return resolutionTask.Id;
        }

        await _coordinator.ExecutePhaseAsync(resolutionTask, ActionExecutionPhase.Immediate, cancellationToken);

        return resolutionTask.Id;
    }
}
