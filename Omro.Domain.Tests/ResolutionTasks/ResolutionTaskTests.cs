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

    [Fact]
    public void Resolve_FromActionProposedOrWaitingForExternalDataOrNeedsHumanReview_ShouldSetResolvedAndTimestamp()
    {
        var task1 = new ResolutionTask(Guid.NewGuid(), new AnomalyEvent(0.9m, Guid.NewGuid()));
        task1.StartAnalysis();
        task1.ProposePlan(new CognitivePlan("T", "R", 0.9m, [new ProposedAction("ACT", "https://a.test/hook", new())]), 10, 10);
        task1.Resolve("Done");
        task1.Status.Should().Be(ResolutionStatus.Resolved);
        task1.ResolvedAt.Should().NotBeNull();

        var task2 = new ResolutionTask(Guid.NewGuid(), new AnomalyEvent(0.6m, Guid.NewGuid()));
        task2.StartAnalysis();
        task2.ProposePlan(new CognitivePlan("T", "R", 0.8m, [new ProposedAction("ACT", "https://a.test/hook", new())], ExpectsExternalApproval: true), 10, 10);
        task2.MarkWaitingForApproval();
        task2.Status.Should().Be(ResolutionStatus.WaitingForExternalData);
        task2.Resolve();
        task2.Status.Should().Be(ResolutionStatus.Resolved);

        var task3 = new ResolutionTask(Guid.NewGuid(), new AnomalyEvent(0.6m, Guid.NewGuid()));
        task3.StartAnalysis();
        task3.MarkNeedsHumanReview("Fallback");
        task3.Resolve("Human closed");
        task3.Status.Should().Be(ResolutionStatus.Resolved);
    }

    [Fact]
    public void Fail_AndTerminalGuards_ShouldEnforceStateMachineAndRejectEmptyReason()
    {
        var task = new ResolutionTask(Guid.NewGuid(), new AnomalyEvent(0.9m, Guid.NewGuid()));
        ((Action)(() => task.Resolve())).Should().Throw<InvalidOperationException>();
        ((Action)(() => task.Fail("   "))).Should().Throw<ArgumentException>();
        ((Action)(() => task.Fail("Ingested fail"))).Should().Throw<InvalidOperationException>();

        task.StartAnalysis();
        task.Fail("400 Bad Request");
        task.Status.Should().Be(ResolutionStatus.Failed);
        task.FailureReason.Should().Be("400 Bad Request");

        ((Action)(() => task.Resolve())).Should().Throw<InvalidOperationException>();
        ((Action)(() => task.Fail("Second fail"))).Should().Throw<InvalidOperationException>();

        var resolvedTask = new ResolutionTask(Guid.NewGuid(), new AnomalyEvent(0.9m, Guid.NewGuid()));
        resolvedTask.StartAnalysis();
        resolvedTask.ProposePlan(new CognitivePlan("T", "R", 0.9m, []), 5, 5);
        resolvedTask.Resolve();
        ((Action)(() => resolvedTask.Fail("Cannot fail after resolved"))).Should().Throw<InvalidOperationException>();
        ((Action)(() => resolvedTask.Resolve("Double resolve"))).Should().Throw<InvalidOperationException>();
    }
}