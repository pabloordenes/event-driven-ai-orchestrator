using System.Text.Json;

namespace Omro.Contracts.Plans;

public static class PlanContractVersions
{
    public const string V1Legacy = "1.0";
    public const string V2Structured = "2.0";

    public static bool IsSupported(string? version) =>
        !string.IsNullOrWhiteSpace(version) &&
        (string.Equals(version.Trim(), V1Legacy, StringComparison.Ordinal) ||
         string.Equals(version.Trim(), V2Structured, StringComparison.Ordinal));
}

public sealed record DeferredActionContract(
    string ActionType,
    string Endpoint,
    string HttpMethod,
    JsonElement Payload
);

public sealed record StructuredAiPlan(
    string SchemaVersion,
    string Title,
    string Reasoning,
    decimal ConfidenceScore,
    Guid? OmroTaskId,
    string? MatchedRuleId,
    int? PolicyVersion,
    DateTimeOffset? ProposalExpiresAt,
    bool IsShadowModeFallback,
    IReadOnlyList<DeferredActionContract> DeferredActions
);
