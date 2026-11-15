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

    public IngestAnomalyCommandHandler(
        IResolutionTaskRepository repository, 
        IUnitOfWork unitOfWork,
        ICognitiveOrchestrator orchestrator)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
        _orchestrator = orchestrator;
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

                                     REGLAS DE NEGOCIO PARA SOBRECUPOS (OVERBOOKING):
                                     1. Si la probabilidad de riesgo de inasistencia (RiskProbability) es MAYOR o IGUAL a 0.85:
                                        - Debes autorizar un sobrecupo automático para no perder la hora del médico.
                                        - ActionType: "GENERATE_OVERBOOK"
                                        - Endpoint: "https://api.medcareos.com/v1/appointments/overbook"
                                        - Payload: Debe incluir "resourceId", "targetRisk": <el valor del riesgo>, y "autoApprove": true.

                                     2. Si la probabilidad de riesgo es MENOR a 0.85:
                                        - No es seguro hacer sobrecupo. Solo mantendremos la cita en observación.
                                        - ActionType: "MONITOR_APPOINTMENT"
                                        - Endpoint: "https://api.medcareos.com/v1/appointments/monitor"
                                        - Payload: Debe incluir "resourceId" y "requiresAction": false.
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
        
        return resolutionTask.Id;
    }
}