namespace Omro.Domain.ResolutionTasks;

public class ResolutionTask
{
    public Guid Id { get; private set; }
    public ResolutionStatus Status { get; private set; }
    public AnomalyEvent Anomaly { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    private readonly List<AgentReasoningTrace> _reasoningTraces = new(); // creamos lista privada que solo la clase puede modificar
    public IReadOnlyList<AgentReasoningTrace> ReasoningTraces => _reasoningTraces.AsReadOnly(); // exponemos la lista como readonly

    public ResolutionTask(AnomalyEvent anomaly)
    {
        Id = Guid.NewGuid();
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
}