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
    private readonly IAiPlanContractParser _planParser;
    private readonly IActionIdempotencyKeyGenerator _idempotencyKeyGenerator;
    private readonly ILogger<ResolutionApprovedConsumer> _logger;

    public ResolutionApprovedConsumer(
        IWebhookDispatcher webhookDispatcher,
        IResolutionTaskRepository taskRepository,
        IActionExecutionRepository actionExecutionRepository,
        IUnitOfWork unitOfWork,
        IActionExecutionCoordinator coordinator,
        ITenantConfigProvider tenantConfigProvider,
        IPlanValidator planValidator,
        IAiPlanContractParser planParser,
        IActionIdempotencyKeyGenerator idempotencyKeyGenerator,
        ILogger<ResolutionApprovedConsumer> logger)
    {
        _webhookDispatcher = webhookDispatcher;
        _taskRepository = taskRepository;
        _actionExecutionRepository = actionExecutionRepository;
        _unitOfWork = unitOfWork;
        _coordinator = coordinator;
        _tenantConfigProvider = tenantConfigProvider;
        _planValidator = planValidator;
        _planParser = planParser;
        _idempotencyKeyGenerator = idempotencyKeyGenerator;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<ResolutionApproved> context)
    {
        var message = context.Message;
        var cancellationToken = context.CancellationToken;

        _logger.LogInformation(
            "OMRO: Aprobación HITL recibida para tarea {TaskId}, recurso {ResourceId} (SchemaVersion={SchemaVersion}). Parseando contrato...",
            message.ResolutionTaskId,
            message.ResourceId,
            message.SchemaVersion);

        var parseResult = _planParser.Parse(message);

        ResolutionTask? existingTask = null;
        if (parseResult.IsSuccess &&
            parseResult.Plan?.OmroTaskId is Guid omroTaskId &&
            omroTaskId != Guid.Empty)
        {
            existingTask = await _taskRepository.GetByIdAsync(omroTaskId, cancellationToken);
        }

        existingTask ??= await _taskRepository.GetByIdAsync(message.ResolutionTaskId, cancellationToken)
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

        if (!parseResult.IsSuccess || parseResult.Plan is null)
        {
            var metadataJson = JsonSerializer.Serialize(new
            {
                errorCode = parseResult.ErrorCode,
                errorMessage = parseResult.ErrorMessage,
                schemaVersion = message.SchemaVersion
            });

            await HandleInvalidDeferredPlanAsync(
                existingTask,
                $"[{parseResult.ErrorCode}] {parseResult.ErrorMessage}",
                metadataJson,
                cancellationToken,
                stepType: "PlanContractParseFailed");
            return;
        }

        var parsedPlan = parseResult.Plan;

        _logger.LogInformation(
            "OMRO: Plan aprobado parseado para tarea {TaskId}, recurso {ResourceId}. ContractVersion={ContractVersion}, IsLegacyFormat={IsLegacyFormat}, DeferredActions={Count}.",
            existingTask?.Id ?? message.ResolutionTaskId,
            message.ResourceId,
            parsedPlan.SchemaVersion,
            parsedPlan.IsLegacyFormat,
            parsedPlan.DeferredActions.Count);

        var effectiveExpiresAt = parsedPlan.ProposalExpiresAt ?? existingTask?.ProposalExpiresAt;
        if (effectiveExpiresAt.HasValue && effectiveExpiresAt.Value < DateTimeOffset.UtcNow)
        {
            _logger.LogWarning(
                "OMRO: Aprobación HITL tardía para tarea {TaskId}, recurso {ResourceId}. ProposalExpiresAt={ProposalExpiresAt} ya había vencido, pero se permite ejecución explícita aprobada.",
                existingTask?.Id ?? message.ResolutionTaskId,
                message.ResourceId,
                effectiveExpiresAt.Value);
        }

        if (parsedPlan.DeferredActions.Count == 0)
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
                "OMRO: Se recibió aprobación para {TaskId} pero no había DeferredActions en el contrato.",
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

        var validation = _planValidator.ValidateDeferredActions(tenantConfig, parsedPlan.DeferredActions);
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
                action.HttpMethod,
                action.PayloadJson,
                _idempotencyKeyGenerator.Generate(
                    existingTask.Id,
                    ActionExecutionPhase.Deferred,
                    index,
                    action.ActionType,
                    action.HttpMethod,
                    action.Endpoint)
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

            var idempotencyKey = _idempotencyKeyGenerator.Generate(
                message.ResolutionTaskId,
                ActionExecutionPhase.Deferred,
                i,
                action.ActionType,
                action.HttpMethod,
                action.Endpoint);
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
                message.ResolutionTaskId,
                new WebhookDispatchOptions(
                    TenantId: message.TenantId,
                    HttpMethod: action.HttpMethod,
                    ActionType: action.ActionType));

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
        CancellationToken cancellationToken,
        string stepType = "PlanValidationFailed")
    {
        _logger.LogError("[OMRO DEFERRED PLAN REJECTED] ({StepType}) {Reason}", stepType, reason);

        if (existingTask is not null)
        {
            if (existingTask.Status == ResolutionStatus.NeedsHumanReview)
            {
                existingTask.RecordTrace("PlanValidator", stepType, reason, metadataJson);
            }
            else
            {
                existingTask.MarkNeedsHumanReview(
                    reason,
                    agentName: "PlanValidator",
                    stepType: stepType,
                    metadataJson: metadataJson);
            }

            await _unitOfWork.SaveChangesAsync(cancellationToken);
            return;
        }

        throw new PermanentWebhookException($"Plan diferido rechazado por catálogo: {reason}", null);
    }
}
