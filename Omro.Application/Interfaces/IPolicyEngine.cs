using Omro.Application.Policies;

namespace Omro.Application.Interfaces;

public interface IPolicyEngine
{
    PolicyDecision Evaluate(PolicyEvaluationInput input);
}
