using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Omro.Application.Common.Exceptions;
using Omro.Application.Interfaces;
using Omro.Domain.Repositories;
using Omro.Domain.ResolutionTasks;

namespace Omro.Application.ResolutionTasks.Services;

public class WebhookExecutionOptions
{
    public const string SectionName = "WebhookExecution";

    public int MaxTotalAttempts { get; set; } = 12;
}

public interface IActionExecutionCoordinator
{
    Task ExecutePhaseAsync(
        ResolutionTask task,
        ActionExecutionPhase phase,
        CancellationToken cancellationToken = default);
}

public class ActionExecutionCoordinator : IActionExecutionCoordinator
{
    private readonly IWebhookDispatcher _webhookDispatcher;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<ActionExecutionCoordinator> _logger;
    private readonly WebhookExecutionOptions _options;

    public ActionExecutionCoordinator(
        IWebhookDispatcher webhookDispatcher,
        IUnitOfWork unitOfWork,
        ILogger<ActionExecutionCoordinator> logger,
        IOptions<WebhookExecutionOptions>? options = null)
    {
        _webhookDispatcher = webhookDispatcher;
        _unitOfWork = unitOfWork;
        _logger = logger;
        _options = options?.Value ?? new WebhookExecutionOptions();
    }

    public async Task ExecutePhaseAsync(
        ResolutionTask task,
        ActionExecutionPhase phase,
        CancellationToken cancellationToken = default)
    {
        var phaseExecutions = task.ActionExecutions
            .Where(e => e.Phase == phase)
            .OrderBy(e => e.ActionIndex)
            .ToList();

        var pendingExecutions = phaseExecutions
            .Where(e => e.Status is not ActionExecutionStatus.Succeeded and not ActionExecutionStatus.FailedPermanent)
            .ToList();

        foreach (var execution in pendingExecutions)
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
                task.Id);

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
            }

            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }

        var permanentFailure = phaseExecutions.FirstOrDefault(e => e.Status == ActionExecutionStatus.FailedPermanent);
        if (permanentFailure is not null)
        {
            var reason = permanentFailure.Error ??
                         $"Fallo permanente al despachar acción {permanentFailure.ActionIndex} hacia {permanentFailure.Endpoint} (HTTP {permanentFailure.HttpStatus}).";
            task.Fail(reason);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            _logger.LogError(
                "ResolutionTask {TaskId} cerrada como Failed por error HTTP permanente en acción {ActionIndex}: {Reason}",
                task.Id,
                permanentFailure.ActionIndex,
                reason);
            return;
        }

        var transientFailure = phaseExecutions.FirstOrDefault(e => e.Status == ActionExecutionStatus.FailedTransient);
        if (transientFailure is not null)
        {
            var reason = transientFailure.Error ??
                         $"Fallo transitorio al despachar acción {transientFailure.ActionIndex} hacia {transientFailure.Endpoint}.";

            if (transientFailure.Attempts >= Math.Max(1, _options.MaxTotalAttempts))
            {
                task.Fail($"Presupuesto de reintentos agotado ({transientFailure.Attempts} intentos): {reason}");
                await _unitOfWork.SaveChangesAsync(cancellationToken);

                _logger.LogCritical(
                    "ResolutionTask {TaskId} marcada como Failed tras agotar {Attempts} intentos en acción {ActionIndex}.",
                    task.Id,
                    transientFailure.Attempts,
                    transientFailure.ActionIndex);
            }

            throw new TransientWebhookDispatchException(reason, transientFailure.HttpStatus);
        }

        // Todas las acciones de esta fase están Succeeded (o no había acciones)
        if (phase == ActionExecutionPhase.Immediate && task.ExpectsExternalApproval)
        {
            if (task.Status == ResolutionStatus.ActionProposed)
            {
                task.MarkWaitingForApproval();
                await _unitOfWork.SaveChangesAsync(cancellationToken);
            }
        }
        else if (task.Status is ResolutionStatus.ActionProposed or ResolutionStatus.WaitingForExternalData or ResolutionStatus.NeedsHumanReview)
        {
            task.Resolve($"Fase {phase} completada exitosamente ({phaseExecutions.Count} acción/es).");
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }
    }
}
