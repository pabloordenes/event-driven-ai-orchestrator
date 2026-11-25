using System.Text.Json;
using MassTransit;
using Microsoft.Extensions.Logging;
using Omro.Application.Common.Exceptions;
using Omro.Application.Interfaces;
using Omro.Application.ResolutionTasks.Services;
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
    private readonly ILogger<ResolutionApprovedConsumer> _logger;

    public ResolutionApprovedConsumer(
        IWebhookDispatcher webhookDispatcher,
        IResolutionTaskRepository taskRepository,
        IActionExecutionRepository actionExecutionRepository,
        IUnitOfWork unitOfWork,
        IActionExecutionCoordinator coordinator,
        ILogger<ResolutionApprovedConsumer> logger)
    {
        _webhookDispatcher = webhookDispatcher;
        _taskRepository = taskRepository;
        _actionExecutionRepository = actionExecutionRepository;
        _unitOfWork = unitOfWork;
        _coordinator = coordinator;
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

        if (existingTask is not null)
        {
            if (existingTask.Status is ResolutionStatus.Resolved or ResolutionStatus.Failed)
            {
                _logger.LogInformation(
                    "OMRO: ResolutionTask {TaskId} ya está en estado terminal {Status}. Se ignora aprobación duplicada.",
                    existingTask.Id,
                    existingTask.Status);
                return;
            }

            if (plan?.DeferredActions is null || plan.DeferredActions.Count == 0)
            {
                _logger.LogWarning(
                    "OMRO: Aprobación sin DeferredActions para tarea {TaskId}. Marcando Resolved.",
                    existingTask.Id);
                existingTask.Resolve("Aprobación externa recibida sin acciones diferidas pendientes.");
                await _unitOfWork.SaveChangesAsync(cancellationToken);
                return;
            }

            var deferredSpecs = plan.DeferredActions.Select((action, index) => (
                action.Endpoint,
                JsonSerializer.Serialize(action.Payload ?? new Dictionary<string, object>()),
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

        if (plan?.DeferredActions is null || plan.DeferredActions.Count == 0)
        {
            _logger.LogWarning(
                "OMRO: Se recibió aprobación para {TaskId} pero no había DeferredActions en el JSON.",
                message.ResolutionTaskId);
            return;
        }

        // Fallback cuando el ResolutionApproved no tiene tarea vinculada en OMRO
        for (int i = 0; i < plan.DeferredActions.Count; i++)
        {
            var action = plan.DeferredActions[i];
            if (string.IsNullOrWhiteSpace(action.Endpoint))
                continue;

            var idempotencyKey = $"{message.ResolutionTaskId}:deferred:{i}";
            _logger.LogInformation(
                "OMRO: Ejecutando Acción Diferida hacia {Endpoint} (Idempotency-Key: {IdempotencyKey})",
                action.Endpoint,
                idempotencyKey);

            var result = await _webhookDispatcher.DispatchAsync(
                action.Endpoint,
                action.Payload ?? new Dictionary<string, object>(),
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

    private record DeferredPlanDto(List<WebhookActionDto> DeferredActions);
    private record WebhookActionDto(string Endpoint, Dictionary<string, object> Payload);
}
