namespace Omro.Application.Interfaces;

public interface ILlmCostCalculator
{
    decimal CalculateEstimatedCostUsd(
        string providerName,
        string modelName,
        int promptTokens,
        int completionTokens);
}
