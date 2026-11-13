namespace Omro.Domain.ResolutionTasks;

public class AgentReasoningTrace
{
    public Guid Id { get; private set; }
    public Guid ResolutionTaskId { get; private set; }
    public string AgentName { get; private set; }
    public string StepType { get; private set; }
    public string TraceMessage { get; private set; } = string.Empty;
    public string? MetadataJson { get; private set; } // mapeado a JSONB en postgreSQL
    public int PromptTokens { get; private set; }
    public int CompletionTokens { get; private set; }
    public DateTime Timestamp { get; private set; }

    internal AgentReasoningTrace(
        Guid resolutionTaskId,
        string agentName,
        string stepType,
        string traceMessage,
        string? metadataJson = null,
        int promptTokens = 0,
        int completionTokens = 0
        )
    {
        Id = Guid.NewGuid();
        ResolutionTaskId = resolutionTaskId;
        AgentName = agentName;
        StepType = stepType;
        TraceMessage = traceMessage;
        MetadataJson = metadataJson;
        PromptTokens = promptTokens;
        CompletionTokens = completionTokens;
        Timestamp = DateTime.UtcNow;
    }
    
    private AgentReasoningTrace() { } // ef core
}