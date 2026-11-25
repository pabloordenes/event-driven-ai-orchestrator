namespace Omro.Infrastructure.Configuration;

public class PlanValidationOptions
{
    public const string SectionName = "PlanValidation";

    public int MaxActionsPerPlan { get; set; } = 10;
    public int MaxJsonDepth { get; set; } = 16;
    public bool AllowInsecureHttpEndpoints { get; set; } = false;
}
