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

        // Simulamos las reglas de negocio extraídas desde el catálogo/políticas del Tenant (E5 reemplazará este fixture por BD)
        string tenantPromptContext = """
                                     Eres el sistema de decisiones de OMRO para el tenant configurado.
                                     Tu objetivo es seleccionar acciones exclusivamente desde el catálogo autorizado según score y atributos genéricos del evento.
                                     NOTA: Solo recibes anomalías operativas relevantes (RiskProbability >= 0.50).

                                     REGLAS DE DECISIÓN DEL TENANT:
                                     1. SI `final_notification_delivered` == "false":
                                     - Prohibido ejecutar acción autónoma sobre el cupo. Deriva a propuesta HITL de monitoreo manual.
                                     - ExpectsExternalApproval: true
                                     - ActionType: "PROPOSE_RESOLUTION"
                                     - Endpoint: "https://medcare-cqrs-clinical-api.onrender.com/api/resolutions/propose"
                                     - Payload: { "resourceId": "<ResourceId>", "riskProbability": <RiskProbability>, "aiPlanJson": "{\"DeferredActions\":[{\"Endpoint\":\"https://medcare-cqrs-clinical-api.onrender.com/api/appointments/monitor\",\"Payload\":{\"resourceId\":\"<ResourceId>\",\"requiresAction\":true,\"reason\":\"Confirmación previa requerida no entregada\"}}]}" }

                                     2. SI `RiskProbability` >= 0.85 AND `auto_overbook_enabled` == "true" AND `final_notification_delivered` == "true" AND `clinical_exclusion` != "true":
                                     - Autoriza ejecución autónoma inmediata desde el catálogo del tenant.
                                     - ExpectsExternalApproval: false
                                     - ActionType: "GENERATE_OVERBOOK"
                                     - Endpoint: "https://medcare-cqrs-clinical-api.onrender.com/api/appointments/overbook"
                                     - Payload: { "resourceId": "<ResourceId>", "reason": "Automated decision from risk threshold and event attributes", "correlationId": "<CorrelationId>", "decisionMode": "Autonomous" }

                                     3. EN CUALQUIER OTRO CASO (0.50 <= RiskProbability < 0.85, o `clinical_exclusion` == "true", o `auto_overbook_enabled` != "true" en modo sombra):
                                     - Envía una propuesta HITL para aprobación humana externa.
                                     - ExpectsExternalApproval: true
                                     - ActionType: "PROPOSE_RESOLUTION"
                                     - Endpoint: "https://medcare-cqrs-clinical-api.onrender.com/api/resolutions/propose"
                                     - Payload: { "resourceId": "<ResourceId>", "riskProbability": <RiskProbability>, "aiPlanJson": "{\"DeferredActions\":[{\"Endpoint\":\"https://medcare-cqrs-clinical-api.onrender.com/api/appointments/overbook\",\"Payload\":{\"resourceId\":\"<ResourceId>\",\"reason\":\"Approved HITL action\",\"correlationId\":\"<CorrelationId>\",\"decisionMode\":\"Approved\"}}]}" }
                                     """;

        // razonamiento cognitivo: el modelo asume el control con atributos agnósticos del evento
        var (plan, promptTokens, completionTokens) = request.Attributes is { Count: > 0 }
            ? await _orchestrator.ProposeResolutionAsync(
                anomalyEvent,
                tenantPromptContext,
                request.Attributes,
                cancellationToken)
            : await _orchestrator.ProposeResolutionAsync(
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
