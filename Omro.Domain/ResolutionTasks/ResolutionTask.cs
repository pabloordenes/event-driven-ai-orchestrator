using System.Text.Json;

namespace Omro.Domain.ResolutionTasks;

public class ResolutionTask
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public ResolutionStatus Status { get; private set; }
    public AnomalyEvent Anomaly { get; private set; }
    public bool ExpectsExternalApproval { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }
    public DateTime? ResolvedAt { get; private set; }
    public string? FailureReason { get; private set; }

    private readonly List<AgentReasoningTrace> _reasoningTraces = new();
    public IReadOnlyList<AgentReasoningTrace> ReasoningTraces => _reasoningTraces.AsReadOnly();

    private readonly List<ActionExecution> _actionExecutions = new();
    public IReadOnlyList<ActionExecution> ActionExecutions => _actionExecutions.AsReadOnly();

    public ResolutionTask(Guid tenantId, AnomalyEvent anomaly)
    {
        Id = Guid.NewGuid();
        TenantId = tenantId;
        Anomaly = anomaly;
        Status = ResolutionStatus.Ingested;
        CreatedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    private ResolutionTask() { } // ef core

    public void StartAnalysis()
    {
        if (Status != ResolutionStatus.Ingested)
        {
            throw new InvalidOperationException("La tarea no está en un estado válido para ser analizada.");
        }

        Status = ResolutionStatus.Analyzing;
        UpdatedAt = DateTime.UtcNow;

        _reasoningTraces.Add(new AgentReasoningTrace(
            resolutionTaskId: Id,
            agentName: "Orchestrator",
            stepType: "SystemState",
            traceMessage: $"[{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}] Estado cambiado a Analyzing. Iniciando motor cognitivo."
        ));
    }

    public void ProposePlan(CognitivePlan plan, int promptTokens, int completionTokens)
    {
        if (Status != ResolutionStatus.Analyzing)
            throw new InvalidOperationException("Solo se pueden proponer planes en estado de análisis.");

        Status = ResolutionStatus.ActionProposed;
        ExpectsExternalApproval = plan.ExpectsExternalApproval;
        UpdatedAt = DateTime.UtcNow;

        if (plan.ProposedActions is { Count: > 0 })
        {
            for (int i = 0; i < plan.ProposedActions.Count; i++)
            {
                var action = plan.ProposedActions[i];
                if (string.IsNullOrWhiteSpace(action.Endpoint))
                    continue;

                var payloadJson = JsonSerializer.Serialize(action.Payload ?? new Dictionary<string, object>());
                _actionExecutions.Add(ActionExecution.CreatePending(
                    Id,
                    ActionExecutionPhase.Immediate,
                    i,
                    action.ActionType,
                    action.Endpoint,
                    payloadJson,
                    $"{Id}:{i}"));
            }
        }

        string metadataJson = JsonSerializer.Serialize(plan);

        _reasoningTraces.Add(new AgentReasoningTrace(
            resolutionTaskId: Id,
            agentName: "GroqCognitiveOrchestrator",
            stepType: "PlanGenerated",
            traceMessage: $"Plan propuesto por IA. Costo: {promptTokens + completionTokens} tokens.",
            metadataJson: metadataJson,
            promptTokens: promptTokens,
            completionTokens: completionTokens
        ));
    }

    public void MarkNeedsHumanReview(string reason, CognitivePlan? fallbackPlan = null)
    {
        if (Status != ResolutionStatus.Analyzing)
            throw new InvalidOperationException("Solo se puede escalar a revisión humana desde el estado de análisis.");

        Status = ResolutionStatus.NeedsHumanReview;
        UpdatedAt = DateTime.UtcNow;

        string? metadataJson = fallbackPlan != null ? JsonSerializer.Serialize(fallbackPlan) : null;

        _reasoningTraces.Add(new AgentReasoningTrace(
            resolutionTaskId: Id,
            agentName: "GroqCognitiveOrchestrator",
            stepType: "FallbackNeedsHumanReview",
            traceMessage: $"Escalado a revisión humana sin despacho automático. Motivo: {reason}",
            metadataJson: metadataJson,
            promptTokens: 0,
            completionTokens: 0
        ));
    }

    public IReadOnlyList<ActionExecution> RegisterDeferredActions(
        IEnumerable<(string Endpoint, string PayloadJson, string IdempotencyKey)> actions)
    {
        if (Status == ResolutionStatus.Failed)
            throw new InvalidOperationException($"No se pueden registrar acciones diferidas sobre la tarea {Id} en estado '{Status}'.");

        var existingIndices = _actionExecutions
            .Where(a => a.Phase == ActionExecutionPhase.Deferred)
            .Select(a => a.ActionIndex)
            .ToHashSet();

        var newlyAdded = new List<ActionExecution>();
        int index = 0;

        foreach (var (endpoint, payloadJson, idempotencyKey) in actions)
        {
            if (!string.IsNullOrWhiteSpace(endpoint) && !existingIndices.Contains(index))
            {
                var execution = ActionExecution.CreatePending(
                    Id,
                    ActionExecutionPhase.Deferred,
                    index,
                    "DEFERRED_WEBHOOK",
                    endpoint,
                    payloadJson,
                    idempotencyKey);
                _actionExecutions.Add(execution);
                newlyAdded.Add(execution);
            }

            index++;
        }

        if (newlyAdded.Count > 0)
        {
            UpdatedAt = DateTime.UtcNow;
        }

        return newlyAdded;
    }

    public void MarkWaitingForApproval(string? note = null)
    {
        if (Status != ResolutionStatus.ActionProposed)
            throw new InvalidOperationException($"Solo tareas en 'ActionProposed' pueden pasar a 'WaitingForExternalData' (estado actual: '{Status}').");

        Status = ResolutionStatus.WaitingForExternalData;
        UpdatedAt = DateTime.UtcNow;

        _reasoningTraces.Add(new AgentReasoningTrace(
            resolutionTaskId: Id,
            agentName: "Orchestrator",
            stepType: "WaitingForExternalData",
            traceMessage: string.IsNullOrWhiteSpace(note)
                ? "Acciones iniciales completadas. Esperando aprobación externa antes de acciones diferidas."
                : note.Trim()
        ));
    }

    public void Resolve(string? summary = null)
    {
        if (Status is not (ResolutionStatus.ActionProposed or ResolutionStatus.WaitingForExternalData or ResolutionStatus.NeedsHumanReview))
        {
            throw new InvalidOperationException(
                $"No se puede resolver la tarea {Id} desde el estado '{Status}'.");
        }

        var utcNow = DateTime.UtcNow;
        Status = ResolutionStatus.Resolved;
        ResolvedAt = utcNow;
        UpdatedAt = utcNow;

        _reasoningTraces.Add(new AgentReasoningTrace(
            resolutionTaskId: Id,
            agentName: "Orchestrator",
            stepType: "Resolved",
            traceMessage: string.IsNullOrWhiteSpace(summary)
                ? "Todas las acciones de la resolución fueron completadas exitosamente."
                : summary.Trim()
        ));
    }

    public void Fail(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("El motivo de fallo no puede estar vacío.", nameof(reason));

        if (Status is not (ResolutionStatus.Analyzing or ResolutionStatus.ActionProposed or ResolutionStatus.WaitingForExternalData or ResolutionStatus.NeedsHumanReview))
        {
            throw new InvalidOperationException(
                $"No se puede marcar como fallida la tarea {Id} desde el estado '{Status}'.");
        }

        var trimmed = reason.Trim();
        var bounded = trimmed.Length <= 1000 ? trimmed : trimmed[..1000];
        var utcNow = DateTime.UtcNow;

        Status = ResolutionStatus.Failed;
        FailureReason = bounded;
        UpdatedAt = utcNow;

        _reasoningTraces.Add(new AgentReasoningTrace(
            resolutionTaskId: Id,
            agentName: "Orchestrator",
            stepType: "Failed",
            traceMessage: $"Tarea fallida: {bounded}"
        ));
    }
}
