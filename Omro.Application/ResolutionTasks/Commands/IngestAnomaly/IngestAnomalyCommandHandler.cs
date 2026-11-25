using MediatR;
using Microsoft.Extensions.Logging;
using Omro.Application.Interfaces;
using Omro.Application.Policies;
using Omro.Application.ResolutionTasks.Services;
using Omro.Domain.Exceptions;
using Omro.Domain.Repositories;
using Omro.Domain.ResolutionTasks;

namespace Omro.Application.ResolutionTasks.Commands.IngestAnomaly;

public class IngestAnomalyCommandHandler : IRequestHandler<IngestAnomalyCommand, Guid>
{
    private const int DefaultDedupWindowMinutes = 60;

    private readonly IResolutionTaskRepository _repository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICognitiveOrchestrator _orchestrator;
    private readonly IActionExecutionCoordinator _coordinator;
    private readonly ITenantConfigProvider _tenantConfigProvider;
    private readonly IPolicyEngine _policyEngine;
    private readonly IDeterministicPlanBuilder _planBuilder;
    private readonly IPlanValidator _planValidator;
    private readonly IShadowDecisionRepository _shadowDecisionRepository;
    private readonly IActionIdempotencyKeyGenerator _idempotencyKeyGenerator;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<IngestAnomalyCommandHandler> _logger;

    public IngestAnomalyCommandHandler(
        IResolutionTaskRepository repository,
        IUnitOfWork unitOfWork,
        ICognitiveOrchestrator orchestrator,
        IActionExecutionCoordinator coordinator,
        ITenantConfigProvider tenantConfigProvider,
        IPolicyEngine policyEngine,
        IDeterministicPlanBuilder planBuilder,
        IPlanValidator planValidator,
        IShadowDecisionRepository shadowDecisionRepository,
        ILogger<IngestAnomalyCommandHandler> logger,
        IActionIdempotencyKeyGenerator? idempotencyKeyGenerator = null,
        TimeProvider? timeProvider = null)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
        _orchestrator = orchestrator;
        _coordinator = coordinator;
        _tenantConfigProvider = tenantConfigProvider;
        _policyEngine = policyEngine;
        _planBuilder = planBuilder;
        _planValidator = planValidator;
        _shadowDecisionRepository = shadowDecisionRepository;
        _logger = logger;
        _idempotencyKeyGenerator = idempotencyKeyGenerator ?? new ActionIdempotencyKeyGenerator();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<Guid> Handle(IngestAnomalyCommand request, CancellationToken cancellationToken)
    {
        var utcNow = _timeProvider.GetUtcNow();
        var tenantConfig = await _tenantConfigProvider.GetTenantConfigurationAsync(request.TenantId, cancellationToken);
        var dedupWindowMinutes = tenantConfig?.ResolveDedupWindowMinutes(request.AnomalyType) ?? DefaultDedupWindowMinutes;
        DateTime? windowStartUtc = dedupWindowMinutes > 0
            ? utcNow.UtcDateTime.AddMinutes(-dedupWindowMinutes)
            : null;

        var existingTask = await _repository.FindActiveOrWithinWindowAsync(
            request.TenantId,
            request.ResourceId,
            request.AnomalyType,
            windowStartUtc,
            cancellationToken);

        if (existingTask != null)
        {
            if (existingTask.Status == ResolutionStatus.ActionProposed)
            {
                _logger.LogInformation(
                    "[OMRO RECOVERY] Reanudando tarea existente {TaskId} en estado ActionProposed sin re-invocar política ni LLM.",
                    existingTask.Id);

                await _coordinator.ExecutePhaseAsync(existingTask, ActionExecutionPhase.Immediate, cancellationToken);
                return existingTask.Id;
            }

            _logger.LogInformation(
                "[OMRO IDEMPOTENCIA] Anomalía deduplicada ({TenantId}, {ResourceId}, {AnomalyType}) dentro de ventana ({WindowMinutes}m, CreatedAt={CreatedAtUtc:O}). Retornando tarea existente {TaskId} en estado {Status}.",
                request.TenantId,
                request.ResourceId,
                request.AnomalyType,
                dedupWindowMinutes,
                existingTask.CreatedAt,
                existingTask.Id,
                existingTask.Status);
            return existingTask.Id;
        }

        // rehidratación
        var anomalyEvent = new AnomalyEvent(
            RiskProbability: request.RiskProbability,
            ResourceId: request.ResourceId,
            AnomalyType: request.AnomalyType,
            CorrelationId: request.CorrelationId);

        var resolutionTask = new ResolutionTask(request.TenantId, anomalyEvent, utcNow.UtcDateTime);
        resolutionTask.StartAnalysis();

        if (tenantConfig is null || !tenantConfig.IsActive)
        {
            var reason = tenantConfig is null
                ? $"Tenant '{request.TenantId}' no tiene configuración registrada en OMRO."
                : $"Tenant '{request.TenantId}' ({tenantConfig.TenantSlug}) está inactivo.";

            _logger.LogWarning(
                "[OMRO FAIL-CLOSED] Escalando anomalía {AnomalyType} para recurso {ResourceId} sin orquestación: {Reason}",
                request.AnomalyType,
                request.ResourceId,
                reason);

            resolutionTask.MarkNeedsHumanReview(reason);
            var (_, savedMissingTenantTask) = await TryPersistNewTaskAsync(resolutionTask, windowStartUtc, cancellationToken);
            return savedMissingTenantTask.Id;
        }

        var decision = _policyEngine.Evaluate(new PolicyEvaluationInput(
            Tenant: tenantConfig,
            Anomaly: anomalyEvent,
            Attributes: request.Attributes,
            EvaluatedAtUtc: utcNow));

        resolutionTask.RecordTrace(
            agentName: "PolicyEngine",
            stepType: "PolicyEvaluated",
            traceMessage: $"Regla '{decision.MatchedRuleId}' (v{decision.PolicyVersion}) -> Outcome={decision.Outcome}. {decision.Reason}",
            metadataJson: decision.EvaluationSnapshotJson);

        _logger.LogInformation(
            "Política evaluada para tenant {TenantSlug}, recurso {ResourceId}, anomalía {AnomalyType}: Rule={RuleId}, Outcome={Outcome}, Shadow={HasShadow}",
            tenantConfig.TenantSlug,
            request.ResourceId,
            request.AnomalyType,
            decision.MatchedRuleId,
            decision.Outcome,
            decision.ShadowIntent is not null);

        if (decision.Outcome == PolicyOutcome.NoAction)
        {
            resolutionTask.CompleteWithPolicyNoAction(
                decision.MatchedRuleId,
                decision.Reason,
                decision.EvaluationSnapshotJson);
            var (_, savedNoActionTask) = await TryPersistNewTaskAsync(resolutionTask, windowStartUtc, cancellationToken);
            return savedNoActionTask.Id;
        }

        if (decision.Outcome == PolicyOutcome.EscalateToHumanReview)
        {
            resolutionTask.MarkNeedsHumanReview(
                decision.Reason,
                agentName: "PolicyEngine",
                stepType: "PolicyEscalated",
                metadataJson: decision.EvaluationSnapshotJson);
            var (_, savedEscalatedTask) = await TryPersistNewTaskAsync(resolutionTask, windowStartUtc, cancellationToken);
            return savedEscalatedTask.Id;
        }

        var baseBuildResult = _planBuilder.BuildBasePlan(
            tenantConfig,
            anomalyEvent,
            request.Attributes,
            decision,
            resolutionTask.Id);

        if (!baseBuildResult.IsSuccess || baseBuildResult.Plan is null)
        {
            var errorReason = baseBuildResult.ErrorReason
                ?? "No se pudo construir el plan determinista desde el catálogo del tenant.";
            _logger.LogError(
                "[OMRO CATALOG MISMATCH] Tenant {TenantSlug}, tarea {TaskId}: {Reason}",
                tenantConfig.TenantSlug,
                resolutionTask.Id,
                errorReason);

            resolutionTask.MarkNeedsHumanReview(
                errorReason,
                agentName: "PolicyEngine",
                stepType: "PolicyCatalogMismatch",
                metadataJson: decision.EvaluationSnapshotJson);
            var (_, savedMismatchTask) = await TryPersistNewTaskAsync(resolutionTask, windowStartUtc, cancellationToken);
            return savedMismatchTask.Id;
        }

        var finalPlan = baseBuildResult.Plan;
        int promptTokens = 0;
        int completionTokens = 0;
        string planAgentName = "PolicyEngine";

        if (decision.InvokeLlmEnrichment && tenantConfig.LlmSettings.EnrichmentEnabled)
        {
            string tenantPromptContext = tenantConfig.BuildOrchestratorPromptContext(request.AnomalyType);
            try
            {
                CognitiveOrchestrationResult? telemetryResult = null;
                CognitivePlan llmPlan;
                int llmPromptTokens;
                int llmCompletionTokens;

                if (_orchestrator is ITelemetryCognitiveOrchestrator telemetryOrchestrator)
                {
                    var orchestrationCtx = new CognitiveOrchestrationContext(
                        TenantId: tenantConfig.TenantId,
                        TenantSlug: tenantConfig.TenantSlug,
                        ResolutionTaskId: resolutionTask.Id,
                        PreferredProvider: tenantConfig.LlmSettings.PreferredProvider,
                        TimeoutMs: tenantConfig.LlmSettings.TimeoutMs,
                        DailyTokenBudget: tenantConfig.LlmSettings.DailyTokenBudget);

                    telemetryResult = await telemetryOrchestrator.ProposeResolutionWithTelemetryAsync(
                        anomalyEvent,
                        tenantPromptContext,
                        request.Attributes,
                        orchestrationCtx,
                        cancellationToken);

                    llmPlan = telemetryResult.Plan;
                    llmPromptTokens = telemetryResult.PromptTokens;
                    llmCompletionTokens = telemetryResult.CompletionTokens;
                }
                else
                {
                    (llmPlan, llmPromptTokens, llmCompletionTokens) = request.Attributes is { Count: > 0 }
                        ? await _orchestrator.ProposeResolutionAsync(
                            anomalyEvent,
                            tenantPromptContext,
                            request.Attributes,
                            cancellationToken)
                        : await _orchestrator.ProposeResolutionAsync(
                            anomalyEvent,
                            tenantPromptContext,
                            cancellationToken);
                }

                if (!llmPlan.IsFallback)
                {
                    finalPlan = _planBuilder.MergeLlmEnrichment(tenantConfig, decision, finalPlan, llmPlan);
                    promptTokens = llmPromptTokens;
                    completionTokens = llmCompletionTokens;
                    planAgentName = "HybridOrchestrator";

                    if (telemetryResult is { UsedFallbackProvider: true })
                    {
                        resolutionTask.RecordTrace(
                            agentName: "HybridOrchestrator",
                            stepType: "LlmProviderFallbackSucceeded",
                            traceMessage:
                            $"Enriquecimiento completado mediante proveedor fallback '{telemetryResult.ProviderName}' ({telemetryResult.ModelName}). Tokens: {llmPromptTokens}+{llmCompletionTokens}, costo estimado: ${telemetryResult.EstimatedCostUsd:F6} USD.");
                    }
                }
                else
                {
                    _logger.LogWarning(
                        "[OMRO HYBRID FALLBACK] LLM no disponible para recurso {ResourceId} ({Reason}). Se mantiene ejecución determinista de la política '{RuleId}'.",
                        request.ResourceId,
                        llmPlan.Reasoning,
                        decision.MatchedRuleId);

                    resolutionTask.RecordTrace(
                        agentName: "HybridOrchestrator",
                        stepType: "LlmUnavailableDeterministicFallback",
                        traceMessage: $"LLM degradado ({llmPlan.Reasoning}). Se ejecuta plan base determinista de '{decision.MatchedRuleId}'.");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(
                    ex,
                    "[OMRO HYBRID FALLBACK] Excepción invocando LLM para recurso {ResourceId}. Se mantiene ejecución determinista de la política '{RuleId}'.",
                    request.ResourceId,
                    decision.MatchedRuleId);

                resolutionTask.RecordTrace(
                    agentName: "HybridOrchestrator",
                    stepType: "LlmUnavailableDeterministicFallback",
                    traceMessage: $"Error invocando LLM ({ex.Message}). Se ejecuta plan base determinista de '{decision.MatchedRuleId}'.");
            }
        }

        var validation = _planValidator.ValidateImmediatePlan(tenantConfig, decision, finalPlan);
        if (!validation.IsValid)
        {
            _logger.LogError(
                "[OMRO PLAN VALIDATION FAILED] Tenant {TenantSlug}, tarea {TaskId}, anomalía {AnomalyType}: {Summary}",
                tenantConfig.TenantSlug,
                resolutionTask.Id,
                request.AnomalyType,
                validation.Summary);

            resolutionTask.MarkNeedsHumanReview(
                validation.Summary,
                agentName: "PlanValidator",
                stepType: "PlanValidationFailed",
                metadataJson: validation.ToAuditJson());
            var (_, savedInvalidTask) = await TryPersistNewTaskAsync(resolutionTask, windowStartUtc, cancellationToken);
            return savedInvalidTask.Id;
        }

        if (decision.ShadowIntent is not null)
        {
            var effectiveCorrelationId = request.CorrelationId != Guid.Empty
                ? request.CorrelationId
                : resolutionTask.Id;

            var shadowDecision = ShadowDecision.Create(
                tenantId: request.TenantId,
                resolutionTaskId: resolutionTask.Id,
                resourceId: request.ResourceId,
                anomalyType: request.AnomalyType,
                policyId: decision.PolicyId ?? tenantConfig.TenantId,
                policyVersion: decision.PolicyVersion,
                matchedRuleId: decision.MatchedRuleId,
                riskProbability: request.RiskProbability,
                intendedActionType: decision.ShadowIntent.IntendedActionType,
                actualActionType: decision.ShadowIntent.ActualActionType,
                suppressionReason: decision.ShadowIntent.SuppressionReason,
                evaluationSnapshotJson: decision.EvaluationSnapshotJson,
                correlationId: effectiveCorrelationId,
                evaluatedAtUtc: utcNow);

            _shadowDecisionRepository.Add(shadowDecision);
            resolutionTask.RecordTrace(
                agentName: "PolicyEngine",
                stepType: "PolicyShadowDecision",
                traceMessage: $"Decisión sombra registrada: {decision.ShadowIntent.IntendedActionType} suprimida hacia {decision.ShadowIntent.ActualActionType} ({decision.ShadowIntent.SuppressionReason}).",
                metadataJson: decision.EvaluationSnapshotJson);
        }

        var immediateSpecs = validation.ValidatedActions
            .Select((action, index) => (
                action.ActionType,
                action.Endpoint,
                action.HttpMethod,
                action.PayloadJson,
                _idempotencyKeyGenerator.Generate(
                    resolutionTask.Id,
                    ActionExecutionPhase.Immediate,
                    index,
                    action.ActionType,
                    action.HttpMethod,
                    action.Endpoint)))
            .ToList();

        resolutionTask.ProposePlan(
            finalPlan,
            promptTokens,
            completionTokens,
            planAgentName,
            proposalExpiresAt: decision.ProposalExpiresAt,
            validatedImmediateActions: immediateSpecs);

        // Transacción 1: persistir intención (ResolutionTask + ActionExecutions Pending + ShadowDecision) ANTES de despachar webhooks
        var (persisted, persistedTask) = await TryPersistNewTaskAsync(resolutionTask, windowStartUtc, cancellationToken);
        if (!persisted)
        {
            return persistedTask.Id;
        }

        await _coordinator.ExecutePhaseAsync(persistedTask, ActionExecutionPhase.Immediate, cancellationToken);

        return persistedTask.Id;
    }

    private async Task<(bool Persisted, ResolutionTask Task)> TryPersistNewTaskAsync(
        ResolutionTask candidate,
        DateTime? windowStartUtc,
        CancellationToken cancellationToken)
    {
        _repository.Add(candidate);
        try
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            return (true, candidate);
        }
        catch (DuplicateActiveTaskException ex)
        {
            var winner = await _repository.FindActiveOrWithinWindowAsync(
                candidate.TenantId,
                candidate.Anomaly.ResourceId,
                candidate.Anomaly.AnomalyType,
                windowStartUtc,
                cancellationToken);

            if (winner is not null)
            {
                _logger.LogInformation(
                    ex,
                    "[OMRO DEDUP RACE] Conflicto concurrente detectado para ({TenantId}, {ResourceId}, {AnomalyType}). Recuperando tarea ganadora {WinnerTaskId} sin despachar webhooks.",
                    candidate.TenantId,
                    candidate.Anomaly.ResourceId,
                    candidate.Anomaly.AnomalyType,
                    winner.Id);

                return (false, winner);
            }

            throw;
        }
    }
}
