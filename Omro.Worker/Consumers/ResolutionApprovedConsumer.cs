using System.Text.Json;
using MassTransit;
using Microsoft.Extensions.Logging;
using Omro.Application.Common.Exceptions;
using Omro.Application.Interfaces;
using Omro.Application.ResolutionTasks.Services;
using Omro.Application.Validation;
using Omro.Contracts.Events;
using Omro.Domain.Repositories;
using Omro.Domain.ResolutionTasks;

namespace Omro.Worker.Consumers;

public class ResolutionApprovedConsumer : IConsumer<ResolutionApproved>
{
    private readonly IWebhookDispatcher _webhookDispatcher;
    private readonly IResolutionTaskRepository _taskRepository;
    private readonly IActionExecutionRepository _actionExecutionRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IActionExecutionCoordinator _coordinator;
    private readonly ITenantConfigProvider _tenantConfigProvider;
    private readonly IPlanValidator _planValidator;
    private readonly ILogger<ResolutionApprovedConsumer> _logger;

    public ResolutionApprovedConsumer(
        IWebhookDispatcher webhookDispatcher,
        IResolutionTaskRepository taskRepository,
        IActionExecutionRepository actionExecutionRepository,
        IUnitOfWork unitOfWork,
        IActionExecutionCoordinator coordinator,
        ITenantConfigProvider tenantConfigProvider,
        IPlanValidator planValidator,
        ILogger<ResolutionApprovedConsumer> logger)
    {
        _webhookDispatcher = webhookDispatcher;
        _taskRepository = taskRepository;
        _actionExecutionRepository = actionExecutionRepository;
        _unitOfWork = unitOfWork;
        _coordinator = coordinator;
        _tenantConfigProvider = tenantConfigProvider;
        _planValidator = planValidator;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<ResolutionApproved> context)
    {
        var message = context.Message;
        var cancellationToken = context.CancellationToken;

        _logger.LogInformation(
            "OMRO: Aprobación HITL recibida para tarea {TaskId}, recurso {ResourceId}. Buscando acciones diferidas...",
            message.ResolutionTaskId,
            message.ResourceId);

        var plan = JsonSerializer.Deserialize<DeferredPlanDto>(
            message.AiPlanJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        var existingTask = await _taskRepository.GetByIdAsync(message.ResolutionTaskId, cancellationToken)
                           ?? await _taskRepository.GetLatestByTenantAndResourceAsync(
                               message.TenantId,
                               message.ResourceId,
                               cancellationToken);

        if (existingTask is not null && existingTask.Status is ResolutionStatus.Resolved or ResolutionStatus.Failed)
        {
            _logger.LogInformation(
                "OMRO: ResolutionTask {TaskId} ya está en estado terminal {Status}. Se ignora aprobación duplicada.",
                existingTask.Id,
                existingTask.Status);
            return;
        }

        if (plan?.DeferredActions is null || plan.DeferredActions.Count == 0)
        {
            if (existingTask is not null)
            {
                _logger.LogWarning(
                    "OMRO: Aprobación sin DeferredActions para tarea {TaskId}. Marcando Resolved.",
                    existingTask.Id);
                existingTask.Resolve("Aprobación externa recibida sin acciones diferidas pendientes.");
                await _unitOfWork.SaveChangesAsync(cancellationToken);
                return;
            }

            _logger.LogWarning(
                "OMRO: Se recibió aprobación para {TaskId} pero no había DeferredActions en el JSON.",
                message.ResolutionTaskId);
            return;
        }

        var tenantConfig = await _tenantConfigProvider.GetTenantConfigurationAsync(message.TenantId, cancellationToken);
        if (tenantConfig is null || !tenantConfig.IsActive)
        {
            var reason = tenantConfig is null
                ? $"Tenant '{message.TenantId}' no tiene configuración registrada en OMRO para validar acciones diferidas."
                : $"Tenant '{message.TenantId}' ({tenantConfig.TenantSlug}) está inactivo.";

            await HandleInvalidDeferredPlanAsync(existingTask, reason, metadataJson: null, cancellationToken);
            return;
        }

        var candidates = plan.DeferredActions
            .Select(a => new DeferredActionCandidate(a.ActionType, a.Endpoint, a.HttpMethod, a.Payload))
            .ToList();

        var validation = _planValidator.ValidateDeferredActions(tenantConfig, candidates);
        if (!validation.IsValid)
        {
            await HandleInvalidDeferredPlanAsync(existingTask, validation.Summary, validation.ToAuditJson(), cancellationToken);
            return;
        }

        if (existingTask is not null)
        {
            var deferredSpecs = validation.ValidatedActions.Select((action, index) => (
                action.ActionType,
                action.Endpoint,
                action.PayloadJson,
                $"{message.ResolutionTaskId}:deferred:{index}"
            ));

            var addedExecutions = existingTask.RegisterDeferredActions(deferredSpecs);
            if (addedExecutions.Count > 0)
            {
                _actionExecutionRepository.AddRange(addedExecutions);
                await _unitOfWork.SaveChangesAsync(cancellationToken);
            }

            await _coordinator.ExecutePhaseAsync(existingTask, ActionExecutionPhase.Deferred, cancellationToken);
            return;
        }

        // Fallback cuando el ResolutionApproved no tiene tarea vinculada en OMRO
        for (int i = 0; i < validation.ValidatedActions.Count; i++)
        {
            var action = validation.ValidatedActions[i];
            var payloadDict = JsonSerializer.Deserialize<Dictionary<string, object>>(action.PayloadJson)
                              ?? new Dictionary<string, object>();

            var idempotencyKey = $"{message.ResolutionTaskId}:deferred:{i}";
            _logger.LogInformation(
                "OMRO: Ejecutando Acción Diferida validada {ActionType} hacia {Endpoint} (Idempotency-Key: {IdempotencyKey})",
                action.ActionType,
                action.Endpoint,
                idempotencyKey);

            var result = await _webhookDispatcher.DispatchAsync(
                action.Endpoint,
                payloadDict,
                cancellationToken,
                idempotencyKey,
                message.ResolutionTaskId);

            if (!result.IsSuccess)
            {
                if (result.IsPermanentFailure)
                {
                    throw new PermanentWebhookException(
                        result.ErrorMessage ?? $"Error permanente en webhook diferido {action.Endpoint}",
                        result.HttpStatusCode);
                }

                throw new TransientWebhookDispatchException(
                    result.ErrorMessage ?? $"Error transitorio en webhook diferido {action.Endpoint}",
                    result.HttpStatusCode);
            }
        }
    }

    private async Task HandleInvalidDeferredPlanAsync(
        ResolutionTask? existingTask,
        string reason,
        string? metadataJson,
        CancellationToken cancellationToken)
    {
        _logger.LogError("[OMRO DEFERRED PLAN REJECTED] {Reason}", reason);

        if (existingTask is not null)
        {
            if (existingTask.Status == ResolutionStatus.NeedsHumanReview)
            {
                existingTask.RecordTrace("PlanValidator", "PlanValidationFailed", reason, metadataJson);
            }
            else
            {
                existingTask.MarkNeedsHumanReview(
                    reason,
                    agentName: "PlanValidator",
                    stepType: "PlanValidationFailed",
                    metadataJson: metadataJson);
            }

            await _unitOfWork.SaveChangesAsync(cancellationToken);
            return;
        }

        throw new PermanentWebhookException($"Plan diferido rechazado por catálogo: {reason}", null);
    }

    private record DeferredPlanDto(List<WebhookActionDto> DeferredActions);
    private record WebhookActionDto(
        string Endpoint,
        Dictionary<string, object>? Payload,
        string? ActionType = null,
        string? HttpMethod = null);
}
