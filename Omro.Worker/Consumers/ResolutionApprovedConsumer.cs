using System.Text.Json;
using MassTransit;
using Microsoft.Extensions.Logging;
using Omro.Application.Common.Exceptions;
using Omro.Application.Interfaces;
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
    private readonly ILogger<ResolutionApprovedConsumer> _logger;

    public ResolutionApprovedConsumer(
        IWebhookDispatcher webhookDispatcher,
        IResolutionTaskRepository taskRepository,
        IActionExecutionRepository actionExecutionRepository,
        IUnitOfWork unitOfWork,
        ILogger<ResolutionApprovedConsumer> logger)
    {
        _webhookDispatcher = webhookDispatcher;
        _taskRepository = taskRepository;
        _actionExecutionRepository = actionExecutionRepository;
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<ResolutionApproved> context)
    {
        var message = context.Message;
        var cancellationToken = context.CancellationToken;

        _logger.LogInformation(
            "OMRO: Aprobación HITL recibida para tarea {TaskId}. Buscando acciones diferidas...",
            message.ResolutionTaskId);

        var plan = JsonSerializer.Deserialize<DeferredPlanDto>(
            message.AiPlanJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (plan?.DeferredActions is null || plan.DeferredActions.Count == 0)
        {
            _logger.LogWarning(
                "OMRO: Se recibió aprobación para {TaskId} pero no había DeferredActions en el JSON.",
                message.ResolutionTaskId);
            return;
        }

        var existingTask = await _taskRepository.GetByIdAsync(message.ResolutionTaskId, cancellationToken);
        if (existingTask is not null)
        {
            var existingDeferred = await _actionExecutionRepository.GetByTaskAndPhaseAsync(
                existingTask.Id,
                ActionExecutionPhase.Deferred,
                cancellationToken);

            if (existingDeferred.Count == 0)
            {
                for (int i = 0; i < plan.DeferredActions.Count; i++)
                {
                    var action = plan.DeferredActions[i];
                    if (string.IsNullOrWhiteSpace(action.Endpoint))
                        continue;

                    var payloadJson = JsonSerializer.Serialize(action.Payload ?? new Dictionary<string, object>());
                    existingDeferred.Add(ActionExecution.CreatePending(
                        existingTask.Id,
                        ActionExecutionPhase.Deferred,
                        i,
                        "DEFERRED_WEBHOOK",
                        action.Endpoint,
                        payloadJson,
                        $"{message.ResolutionTaskId}:deferred:{i}"));
                }

                if (existingDeferred.Count > 0)
                {
                    _actionExecutionRepository.AddRange(existingDeferred);
                    await _unitOfWork.SaveChangesAsync(cancellationToken);
                }
            }

            var toDispatch = existingDeferred
                .Where(e => e.Status is not ActionExecutionStatus.Succeeded and not ActionExecutionStatus.FailedPermanent)
                .ToList();

            string? transientErr = null;
            int? transientStatus = null;

            foreach (var execution in toDispatch)
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
                    existingTask.Id);

                if (result.IsSuccess)
                {
                    execution.MarkSucceeded(result.HttpStatusCode, result.Attempts, result.ExecutedAtUtc);
                }
                else if (result.IsPermanentFailure)
                {
                    execution.MarkFailedPermanent(result.HttpStatusCode, result.Attempts, result.ErrorMessage, result.ExecutedAtUtc);
                    await _unitOfWork.SaveChangesAsync(cancellationToken);
                    throw new PermanentWebhookException(
                        result.ErrorMessage ?? $"Error permanente en webhook diferido {execution.Endpoint}",
                        result.HttpStatusCode);
                }
                else
                {
                    execution.MarkFailedTransient(result.HttpStatusCode, result.Attempts, result.ErrorMessage, result.ExecutedAtUtc);
                    transientErr ??= result.ErrorMessage;
                    transientStatus ??= result.HttpStatusCode;
                }

                await _unitOfWork.SaveChangesAsync(cancellationToken);
            }

            if (transientErr is not null)
            {
                throw new TransientWebhookDispatchException(transientErr, transientStatus);
            }

            return;
        }

        // Fallback cuando el ResolutionApproved no tiene tarea vinculada por Id en OMRO (p.ej. tests aislados o ID externo)
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
