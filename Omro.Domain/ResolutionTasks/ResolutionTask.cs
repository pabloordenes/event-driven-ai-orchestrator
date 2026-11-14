namespace Omro.Domain.ResolutionTasks;

public class ResolutionTask
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public ResolutionStatus Status { get; private set; }
    public AnomalyEvent Anomaly { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    private readonly List<AgentReasoningTrace> _reasoningTraces = new(); // creamos lista privada que solo la clase puede modificar
    public IReadOnlyList<AgentReasoningTrace> ReasoningTraces => _reasoningTraces.AsReadOnly(); // exponemos la lista como readonly

    public ResolutionTask(Guid tenantId, AnomalyEvent anomaly)
    {
        Id = Guid.NewGuid();
        TenantId = tenantId;
        Anomaly = anomaly;
        Status = ResolutionStatus.Ingested;
        CreatedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }
    
    private ResolutionTask() {} // ef core

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
            traceMessage: $"[{DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")}] Estado cambiado a Analyzing. Iniciando motor cognitivo."
        ));
    }
    
    public void ProposePlan(CognitivePlan plan, int promptTokens, int completionTokens)
    {
        if (Status != ResolutionStatus.Analyzing)
            throw new InvalidOperationException("Solo se pueden proponer planes en estado de análisis.");

        Status = ResolutionStatus.ActionProposed;
        UpdatedAt = DateTime.UtcNow;

        // Convertimos el record (Contrato) a string para guardarlo en la base de datos
        string metadataJson = System.Text.Json.JsonSerializer.Serialize(plan);

        _reasoningTraces.Add(new AgentReasoningTrace(
            resolutionTaskId: Id,
            agentName: "SemanticKernel",
            stepType: "PlanGenerated",
            traceMessage: $"Plan propuesto por IA. Costo: {promptTokens + completionTokens} tokens.",
            metadataJson: metadataJson,
            promptTokens: promptTokens,
            completionTokens: completionTokens
        ));
    }
}