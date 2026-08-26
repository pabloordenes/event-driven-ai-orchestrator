namespace Omro.Domain.ResolutionTasks;

public class ResolutionTask
{
    public Guid Id { get; private set; }
    public ResolutionStatus Status { get; private set; }
    public AnomalyEvent Anomaly { get; private set; }

    private readonly List<string> _reasoningTraces = new(); // creamos lista privada que solo la clase puede modificar
    public IReadOnlyList<string> ReasoningTraces => _reasoningTraces.AsReadOnly(); // exponemos la lista como readonly

    public ResolutionTask(AnomalyEvent anomaly)
    {
        Id = Guid.NewGuid();
        Anomaly = anomaly;
        Status = ResolutionStatus.Ingested;
    }

    public void StartAnalysis()
    {
        if (Status != ResolutionStatus.Ingested)
        {
            throw new InvalidOperationException("La tarea no está en un estado válido para ser analizada.");
        }
        
        Status = ResolutionStatus.Analyzing;
        
        _reasoningTraces.Add($"[{DateTime.UtcNow:O}] Estado cambiado a Analyzing. Iniciando motor cognitivo.");
    }
}