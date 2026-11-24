using System.Text.Json;
using MediatR;
using Microsoft.Extensions.Logging;
using Omro.Application.Common.Exceptions;
using Omro.Application.Interfaces;
using Omro.Domain.Repositories;
using Omro.Domain.ResolutionTasks;

namespace Omro.Application.ResolutionTasks.Commands.IngestAnomaly;

public class IngestAnomalyCommandHandler : IRequestHandler<IngestAnomalyCommand, Guid>
{
    private readonly IResolutionTaskRepository _repository;
    private readonly IActionExecutionRepository _actionExecutionRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICognitiveOrchestrator _orchestrator;
    private readonly IWebhookDispatcher _webhookDispatcher;
    private readonly ILogger<IngestAnomalyCommandHandler> _logger;

    public IngestAnomalyCommandHandler(
        IResolutionTaskRepository repository,
        IActionExecutionRepository actionExecutionRepository,
        IUnitOfWork unitOfWork,
        ICognitiveOrchestrator orchestrator,
        IWebhookDispatcher webhookDispatcher,
        ILogger<IngestAnomalyCommandHandler> logger)
    {
        _repository = repository;
        _actionExecutionRepository = actionExecutionRepository;
        _unitOfWork = unitOfWork;
        _orchestrator = orchestrator;
        _webhookDispatcher = webhookDispatcher;
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
            var existingExecutions = await _actionExecutionRepository.GetByTaskAndPhaseAsync(
                existingTask.Id,
                ActionExecutionPhase.Immediate,
                cancellationToken);

            var pendingRetryExecutions = existingExecutions
                .Where(e => e.Status is not ActionExecutionStatus.Succeeded and not ActionExecutionStatus.FailedPermanent)
                .ToList();

            if (pendingRetryExecutions.Count > 0)
            {
                _logger.LogInformation(
                    "[OMRO RECOVERY] Reanudando {Count} acción(es) inmediata(s) pendiente(s) para tarea existente {TaskId}.",
                    pendingRetryExecutions.Count,
                    existingTask.Id);

                await ExecutePendingActionsAsync(existingTask.Id, pendingRetryExecutions, cancellationToken);
                return existingTask.Id;
            }

            _logger.LogInformation(
                "[OMRO IDEMPOTENCIA] Anomalía duplicada ({TenantId}, {ResourceId}, {AnomalyType}). Retornando tarea existente {TaskId}.",
                request.TenantId,
                request.ResourceId,
                request.AnomalyType,
                existingTask.Id);
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
                                     - ActionType: "GENERATE_OVERBOOK"
                                     - Endpoint: "https://medcare-cqrs-clinical-api.onrender.com/api/appointments/overbook"
                                     - Payload: Debe incluir "resourceId", "targetRisk": <el valor del riesgo>, y "autoApprove": true.

                                     2. RIESGO MODERADO (RiskProbability MAYOR o IGUAL a 0.50 y MENOR a 0.85, es decir >= 0.50 y < 0.85):
                                     - Riesgo de choque de agenda. NO automatices el sobrecupo.
                                     - Envía una propuesta para que el administrador humano revise el caso (HITL).
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
            "Decisión cognitiva generada para recurso {ResourceId}. Title={Title}, IsFallback={IsFallback}, Actions={ActionCount}",
            request.ResourceId,
            plan.Title,
            plan.IsFallback,
            plan.ProposedActions?.Count ?? 0);

        if (plan.IsFallback)
        {
            resolutionTask.MarkNeedsHumanReview(plan.Reasoning, plan);
        }
        else
        {
            resolutionTask.ProposePlan(plan, promptTokens, completionTokens);
        }

        var executions = new List<ActionExecution>();
        if (!plan.IsFallback && plan.ProposedActions is { Count: > 0 })
        {
            for (int i = 0; i < plan.ProposedActions.Count; i++)
            {
                var action = plan.ProposedActions[i];
                if (string.IsNullOrWhiteSpace(action.Endpoint))
                    continue;

                var payloadJson = JsonSerializer.Serialize(action.Payload ?? new Dictionary<string, object>());
                executions.Add(ActionExecution.CreatePending(
                    resolutionTask.Id,
                    ActionExecutionPhase.Immediate,
                    i,
                    action.ActionType,
                    action.Endpoint,
                    payloadJson,
                    $"{resolutionTask.Id}:{i}"));
            }
        }

        // Transacción 1: persistir intención ANTES de despachar webhooks
        _repository.Add(resolutionTask);
        if (executions.Count > 0)
        {
            _actionExecutionRepository.AddRange(executions);
        }
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        if (executions.Count == 0)
        {
            return resolutionTask.Id;
        }

        await ExecutePendingActionsAsync(resolutionTask.Id, executions, cancellationToken);

        return resolutionTask.Id;
    }

    private async Task ExecutePendingActionsAsync(
        Guid taskId,
        List<ActionExecution> executions,
        CancellationToken cancellationToken)
    {
        string? transientError = null;
        int? transientStatus = null;

        foreach (var execution in executions)
        {
            execution.MarkInProgress();
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            var payload = JsonSerializer.Deserialize<Dictionary<string, object>>(execution.PayloadJson)
                          ?? new Dictionary<string, object>();

            var result = await _webhookDispatcher.DispatchAsync(
                execution.Endpoint,
                payload,
                cancellationToken,
                execution.IdempotencyKey,
                taskId);

            if (result.IsSuccess)
            {
                execution.MarkSucceeded(result.HttpStatusCode, result.Attempts, result.ExecutedAtUtc);
            }
            else if (result.IsPermanentFailure)
            {
                execution.MarkFailedPermanent(
                    result.HttpStatusCode,
                    result.Attempts,
                    result.ErrorMessage,
                    result.ExecutedAtUtc);
            }
            else
            {
                execution.MarkFailedTransient(
                    result.HttpStatusCode,
                    result.Attempts,
                    result.ErrorMessage,
                    result.ExecutedAtUtc);
                transientError ??= result.ErrorMessage ?? "Error transitorio en despacho de webhook.";
                transientStatus ??= result.HttpStatusCode;
            }

            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }

        if (transientError is not null)
        {
            throw new TransientWebhookDispatchException(transientError, transientStatus);
        }
    }
}
