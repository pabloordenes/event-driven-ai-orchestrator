using System.Text.Json;
using Omro.Application.Tenants;

namespace Omro.Application.Validation;

public sealed record PlanValidationViolation(
    string Code,
    int? ActionIndex,
    string Path,
    string Message);

public sealed record ValidatedActionSpec(
    string ActionType,
    string Endpoint,
    string HttpMethod,
    string PayloadJson,
    bool RequiresApproval,
    ActionRetryPolicy RetryPolicy);

public sealed record DeferredActionCandidate(
    string? ActionType,
    string Endpoint,
    string? HttpMethod,
    Dictionary<string, object>? Payload);

public sealed record PlanValidationResult(
    bool IsValid,
    IReadOnlyList<PlanValidationViolation> Violations,
    IReadOnlyList<ValidatedActionSpec> ValidatedActions)
{
    public string Summary
    {
        get
        {
            if (IsValid || Violations.Count == 0)
                return "Plan válido.";

            var joined = string.Join(
                "; ",
                Violations.Select(v => $"[{v.Code}] {v.Path}: {v.Message}"));

            return joined.Length <= 500 ? joined : joined[..500];
        }
    }

    public string ToAuditJson()
    {
        return JsonSerializer.Serialize(new
        {
            isValid = IsValid,
            violationCount = Violations.Count,
            violations = Violations
        });
    }

    public static PlanValidationResult Valid(IReadOnlyList<ValidatedActionSpec> validatedActions) =>
        new(true, Array.Empty<PlanValidationViolation>(), validatedActions);

    public static PlanValidationResult Invalid(IReadOnlyList<PlanValidationViolation> violations) =>
        new(false, violations, Array.Empty<ValidatedActionSpec>());
}
