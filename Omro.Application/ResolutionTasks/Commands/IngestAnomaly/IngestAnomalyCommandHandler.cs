using MediatR;
using Omro.Application.Interfaces;
using Omro.Domain.Repositories;
using Omro.Domain.ResolutionTasks;

namespace Omro.Application.ResolutionTasks.Commands.IngestAnomaly;

public class IngestAnomalyCommandHandler : IRequestHandler<IngestAnomalyCommand, Guid>
{
    private readonly IResolutionTaskRepository _repository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICognitiveOrchestrator _orchestrator;
    private readonly IWebhookDispatcher _webhookDispatcher;

    public IngestAnomalyCommandHandler(
        IResolutionTaskRepository repository, 
        IUnitOfWork unitOfWork,
        ICognitiveOrchestrator orchestrator,
        IWebhookDispatcher webhookDispatcher)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
        _orchestrator = orchestrator;
        _webhookDispatcher = webhookDispatcher;
    }
    
    public async Task<Guid> Handle(IngestAnomalyCommand request, CancellationToken cancellationToken)
    {
        // rehidratacion 
        var anomalyEvent = new AnomalyEvent(
            RiskProbability: request.RiskProbability,
            ResourceId: request.ResourceId);
        
        var resolutionTask = new ResolutionTask(request.TenantId, anomalyEvent);
        
        // mutación de dominio
        resolutionTask.StartAnalysis();

        // Simulamos las reglas de negocio extraídas desde la base de datos para este Tenant (Clínica)
        string tenantPromptContext = """
                                     Eres el sistema de decisiones de MedCareOS para la 'Clínica San José'.
                                     Tu objetivo es mitigar el impacto financiero de los pacientes que no asisten a sus citas (No-Shows).
                                     NOTA: Solo recibes citas que ya fueron catalogadas como Anomalías (Riesgo >= 0.50).

                                     REGLAS DE NEGOCIO:
                                     1. RIESGO CRÍTICO (RiskProbability MAYOR o IGUAL a 0.85):
                                     - El paciente casi seguro faltará. Autoriza un sobrecupo automático.
                                     - ActionType: "GENERATE_OVERBOOK"
                                     - Endpoint: "https://medcare-cqrs-clinical-api.onrender.com/api/appointments/overbook"
                                     - Payload: Debe incluir "resourceId", "targetRisk": <el valor del riesgo>, y "autoApprove": true.

                                     2. RIESGO MODERADO (RiskProbability ENTRE 0.50 y 0.84):
                                     - Riesgo de choque de agenda. NO automatices el sobrecupo.
                                     - Envía una propuesta para que el administrador humano revise el caso (HITL).
                                     - ActionType: "PROPOSE_RESOLUTION"
                                     - Endpoint: "https://medcare-cqrs-clinical-api.onrender.com/api/resolutions/propose"
                                     - Payload: Debe incluir "resourceId", "riskProbability": <el valor del riesgo>, y "aiPlanJson" que contenga un string JSON escapado con la siguiente estructura de Acción Diferida:
                                       { "DeferredActions": [ { "Endpoint": "https://medcare-cqrs-clinical-api.onrender.com/api/appointments/monitor", "Payload": { "resourceId": "<id>", "requiresAction": true } } ] }
                                     """;
        
        // razonamiento cognitivo: el modelo asume el control
        var (plan, promptTokens, completionTokens) = await _orchestrator.ProposeResolutionAsync(
            anomalyEvent,
            tenantPromptContext,
            cancellationToken);
        
        Console.WriteLine($"\n[MEDCAREOS DECISIÓN CLÍNICA]\n{System.Text.Json.JsonSerializer.Serialize(plan, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })}\n");
        
        // mutacion de dominio final: empaquetamos el JSON y los costos
        resolutionTask.ProposePlan(plan, promptTokens, completionTokens);
        
        // persistencia
        _repository.Add(resolutionTask);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        
        foreach (var action in plan.ProposedActions)
        {
            if (!string.IsNullOrWhiteSpace(action.Endpoint))
            {
                try 
                {
                    // Disparo 100% ciego con la URL que indicó el LLM
                    await _webhookDispatcher.DispatchAsync(action.Endpoint, action.Payload, cancellationToken);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR WEBHOOK] Falló la entrega hacia {action.Endpoint}: {ex.Message}");
                }
            }
        }
        
        return resolutionTask.Id;
    }
}