using FluentAssertions;
using Omro.Domain.ResolutionTasks;

namespace Omro.Domain.Tests.ResolutionTasks;

public class ResolutionTaskTests
{
    [Fact]
    public void StartAnalysis_WhenStateIsIngested_ShouldChangeToAnalyzing()
    {
        // arrange
        var dummyEvent = new AnomalyEvent(RiskProbability:0.8m, ResourceId:Guid.NewGuid());
        var task = new ResolutionTask(Guid.NewGuid(), dummyEvent);
        
        // act
        task.StartAnalysis();
        
        // assert
        task.Status.Should().Be(ResolutionStatus.Analyzing);
        task.ReasoningTraces.Should().HaveCount(1);
        
        var firstTrace = task.ReasoningTraces.First();
        firstTrace.StepType.Should().Be("SystemState");
        firstTrace.TraceMessage.Should().Contain("Iniciando motor cognitivo");
    }

    [Fact]
    public void StartAnalysis_WhenStateIsNotIngested_ShouldThrowException()
    {
        // arrange
        var dummyEvent = new AnomalyEvent(RiskProbability:0.8m, ResourceId:Guid.NewGuid());
        var task = new ResolutionTask(Guid.NewGuid(), dummyEvent);
        
        // act
        task.StartAnalysis(); // pasamos el estado a analyzing para forzar la excepcion
        Action act = () => task.StartAnalysis();
        
        // assert
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void MarkNeedsHumanReview_WhenStateIsAnalyzing_ShouldSetNeedsHumanReviewAndAddTrace()
    {
        var dummyEvent = new AnomalyEvent(RiskProbability: 0.90m, ResourceId: Guid.NewGuid());
        var task = new ResolutionTask(Guid.NewGuid(), dummyEvent);
        task.StartAnalysis();

        var fallbackPlan = new CognitivePlan(
            Title: "NeedsHumanReview",
            Reasoning: "Groq timeout",
            ConfidenceScore: 0m,
            ProposedActions: new List<ProposedAction>(),
            IsFallback: true);

        task.MarkNeedsHumanReview("Groq timeout", fallbackPlan);

        task.Status.Should().Be(ResolutionStatus.NeedsHumanReview);
        task.ReasoningTraces.Should().Contain(t => t.StepType == "FallbackNeedsHumanReview");
    }

    [Fact]
    public void MarkNeedsHumanReview_WhenStateIsNotAnalyzing_ShouldThrowInvalidOperationException()
    {
        var dummyEvent = new AnomalyEvent(RiskProbability: 0.90m, ResourceId: Guid.NewGuid());
        var task = new ResolutionTask(Guid.NewGuid(), dummyEvent);

        Action act = () => task.MarkNeedsHumanReview("Groq timeout");

        act.Should().Throw<InvalidOperationException>();
    }
}