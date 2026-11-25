using System.Security.Cryptography;
using System.Text;
using Omro.Application.Interfaces;
using Omro.Domain.ResolutionTasks;

namespace Omro.Application.ResolutionTasks.Services;

public sealed class ActionIdempotencyKeyGenerator : IActionIdempotencyKeyGenerator
{
    public string Generate(
        Guid taskId,
        ActionExecutionPhase phase,
        int actionIndex,
        string actionType,
        string httpMethod,
        string endpoint)
    {
        if (taskId == Guid.Empty)
            throw new ArgumentException("TaskId cannot be empty.", nameof(taskId));
        if (actionIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(actionIndex), "ActionIndex cannot be negative.");

        var normalizedActionType = string.IsNullOrWhiteSpace(actionType)
            ? "WEBHOOK"
            : actionType.Trim().ToUpperInvariant();
        var normalizedMethod = string.IsNullOrWhiteSpace(httpMethod)
            ? "POST"
            : httpMethod.Trim().ToUpperInvariant();
        var normalizedEndpoint = string.IsNullOrWhiteSpace(endpoint)
            ? string.Empty
            : endpoint.Trim();

        var canonicalInput = $"{normalizedActionType}|{normalizedMethod}|{normalizedEndpoint}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalInput));
        var fingerprint = Convert.ToHexStringLower(hash.AsSpan(0, 4));
        var phaseCode = phase == ActionExecutionPhase.Immediate ? "i" : "d";

        return $"omro:{taskId:N}:{phaseCode}:{actionIndex}:{fingerprint}";
    }
}
