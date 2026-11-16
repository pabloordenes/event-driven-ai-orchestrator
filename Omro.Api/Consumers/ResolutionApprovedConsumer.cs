using System.Text.Json;
using MassTransit;
using Microsoft.Extensions.Logging;
using Omro.Application.Interfaces;
using Omro.Contracts.Events;

namespace Omro.Api.Consumers;

public class ResolutionApprovedConsumer : IConsumer<ResolutionApproved>
{
    private readonly IWebhookDispatcher _webhookDispatcher;
    private readonly ILogger<ResolutionApprovedConsumer> _logger;

    public ResolutionApprovedConsumer(IWebhookDispatcher webhookDispatcher, ILogger<ResolutionApprovedConsumer> logger)
    {
        _webhookDispatcher = webhookDispatcher;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<ResolutionApproved> context)
    {
        var message = context.Message;
        _logger.LogInformation("OMRO: Aprobación HITL recibida para tarea {TaskId}. Buscando acciones diferidas...", message.ResolutionTaskId);

        // Deserializamos buscando ESPECÍFICAMENTE la caja fuerte "DeferredActions" que el LLM armó
        var plan = JsonSerializer.Deserialize<DeferredPlanDto>(
            message.AiPlanJson, 
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (plan?.DeferredActions != null)
        {
            foreach (var action in plan.DeferredActions)
            {
                if (!string.IsNullOrWhiteSpace(action.Endpoint))
                {
                    try 
                    {
                        // OMRO dispara CUALQUIER webhook (Sobrecupo, Cancelar, WhatsApp) ciegamente
                        _logger.LogInformation("OMRO: Ejecutando Acción Diferida hacia {Endpoint}", action.Endpoint);
                        await _webhookDispatcher.DispatchAsync(action.Endpoint, action.Payload, context.CancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Fallo al despachar webhook diferido hacia {Endpoint}", action.Endpoint);
                    }
                }
            }
        }
        else
        {
            _logger.LogWarning("OMRO: Se recibió aprobación para {TaskId} pero no había DeferredActions en el JSON.", message.ResolutionTaskId);
        }
    }

    // DTOs privados para mapear la estructura de la Acción Diferida
    private record DeferredPlanDto(List<WebhookActionDto> DeferredActions);
    private record WebhookActionDto(string Endpoint, Dictionary<string, object> Payload);
}