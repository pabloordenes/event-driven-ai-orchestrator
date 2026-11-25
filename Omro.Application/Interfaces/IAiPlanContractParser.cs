using Omro.Application.Validation;
using Omro.Contracts.Events;

namespace Omro.Application.Interfaces;

public sealed record ParsedAiPlan(
    string SchemaVersion,
    bool IsLegacyFormat,
    string? Title,
    string? Reasoning,
    Guid? OmroTaskId,
    DateTimeOffset? ProposalExpiresAt,
    IReadOnlyList<DeferredActionCandidate> DeferredActions);

public sealed record AiPlanParseResult(
    bool IsSuccess,
    ParsedAiPlan? Plan,
    string? ErrorCode,
    string? ErrorMessage)
{
    public static AiPlanParseResult Success(ParsedAiPlan plan) =>
        new(true, plan, null, null);

    public static AiPlanParseResult Failure(string errorCode, string errorMessage) =>
        new(false, null, errorCode, errorMessage);
}

public interface IAiPlanContractParser
{
    AiPlanParseResult Parse(ResolutionApproved message);
}
