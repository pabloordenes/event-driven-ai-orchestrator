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
        
        var task = new ResolutionTask(dummyEvent);
        
        // act
        task.StartAnalysis();
        
        // assert
        // verificamos que el estado cambio usando FluentAssertions
        task.Status.Should().Be(ResolutionStatus.Analyzing);
        
        // verificamos que se haya agregado exactamente 1 registro a la lista
        task.ReasoningTraces.Should().HaveCount(1);
        
        // verificamos keywords
        task.ReasoningTraces[0].Should().Contain("Iniciando motor cognitivo");
    }

    [Fact]
    public void StartAnalysis_WhenStateIsNotIngested_ShouldThrowException()
    {
        
        // arrange
        var dummyEvent = new AnomalyEvent(RiskProbability:0.8m, ResourceId:Guid.NewGuid());
        
        var task = new ResolutionTask(dummyEvent);
        
        // act
        task.StartAnalysis(); // pasamos el estado a analyzing para forzar la excepcion
        
        Action act = () => task.StartAnalysis();
        
        // assert
        act.Should().Throw<InvalidOperationException>();
    }
}