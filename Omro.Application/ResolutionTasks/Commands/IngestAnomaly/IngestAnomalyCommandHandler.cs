using MediatR;
using Microsoft.Extensions.Logging;
using Omro.Application.Interfaces;
using Omro.Application.Policies;
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
    private readonly ITenantConfigProvider _tenantConfigProvider;
    private readonly IPolicyEngine _policyEngine;
    private readonly IDeterministicPlanBuilder _planBuilder;
    private readonly IShadowDecisionRepository _shadowDecisionRepository;
    private readonly ILogger<IngestAnomalyCommandHandler> _logger;

    public IngestAnomalyCommandHandler(
        IResolutionTaskRepository repository,
        IUnitOfWork unitOfWork,
        ICognitiveOrchestrator orchestrator,
        IActionExecutionCoordinator coordinator,
        ITenantConfigProvider tenantConfigProvider,
        IPolicyEngine policyEngine,
        IDeterministicPlanBuilder planBuilder,
        IShadowDecisionRepository shadowDecisionRepository,
        ILogger<IngestAnomalyCommandHandler> logger)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
        _orchestrator = orchestrator;
        _coordinator = coordinator;
        _tenantConfigProvider = tenantConfigProvider;
        _policyEngine = policyEngine;
        _planBuilder = planBuilder;
        _shadowDecisionRepository = shadowDecisionRepository;
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
                    "[OMRO RECOVERY] Reanudando tarea existente {TaskId} en estado ActionProposed sin re-invocar política ni LLM.",
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

        // rehidratación
        var anomalyEvent = new AnomalyEvent(
            RiskProbability: request.RiskProbability,
            ResourceId: request.ResourceId,
            AnomalyType: request.AnomalyType,
            CorrelationId: request.CorrelationId);

        var resolutionTask = new ResolutionTask(request.TenantId, anomalyEvent);
        resolutionTask.StartAnalysis();

        var tenantConfig = await _tenantConfigProvider.GetTenantConfigurationAsync(request.TenantId, cancellationToken);
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
            _repository.Add(resolutionTask);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            return resolutionTask.Id;
        }

        var utcNow = DateTimeOffset.UtcNow;
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
            _repository.Add(resolutionTask);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            return resolutionTask.Id;
        }

        if (decision.Outcome == PolicyOutcome.EscalateToHumanReview)
        {
            resolutionTask.MarkNeedsHumanReview(
                decision.Reason,
                agentName: "PolicyEngine",
                stepType: "PolicyEscalated",
                metadataJson: decision.EvaluationSnapshotJson);
            _repository.Add(resolutionTask);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            return resolutionTask.Id;
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
            _repository.Add(resolutionTask);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            return resolutionTask.Id;
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
                var (llmPlan, llmPromptTokens, llmCompletionTokens) = request.Attributes is { Count: > 0 }
                    ? await _orchestrator.ProposeResolutionAsync(
                        anomalyEvent,
                        tenantPromptContext,
                        request.Attributes,
                        cancellationToken)
                    : await _orchestrator.ProposeResolutionAsync(
                        anomalyEvent,
                        tenantPromptContext,
                        cancellationToken);

                if (!llmPlan.IsFallback)
                {
                    finalPlan = _planBuilder.MergeLlmEnrichment(tenantConfig, decision, finalPlan, llmPlan);
                    promptTokens = llmPromptTokens;
                    completionTokens = llmCompletionTokens;
                    planAgentName = "HybridOrchestrator";
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

        resolutionTask.ProposePlan(finalPlan, promptTokens, completionTokens, planAgentName);

        // Transacción 1: persistir intención (ResolutionTask + ActionExecutions Pending + ShadowDecision) ANTES de despachar webhooks
        _repository.Add(resolutionTask);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        await _coordinator.ExecutePhaseAsync(resolutionTask, ActionExecutionPhase.Immediate, cancellationToken);

        return resolutionTask.Id;
    }
}
