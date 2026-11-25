using FluentAssertions;
using Omro.Application.ResolutionTasks.Services;
using Omro.Domain.ResolutionTasks;

namespace Omro.Domain.Tests.ResolutionTasks;

public class ActionIdempotencyKeyGeneratorTests
{
    private readonly ActionIdempotencyKeyGenerator _generator = new();

    [Fact]
    public void Generate_ShouldBeDeterministic_AndStayUnder100CharsEvenWithLongInputs()
    {
        var taskId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var longActionType = new string('A', 100);
        var longEndpoint = $"https://tenant.example.com/api/v2/operations/{new string('p', 500)}";

        var key1 = _generator.Generate(taskId, ActionExecutionPhase.Immediate, 0, longActionType, "post", longEndpoint);
        var key2 = _generator.Generate(taskId, ActionExecutionPhase.Immediate, 0, $"  {longActionType.ToLowerInvariant()} ", "POST", $" {longEndpoint} ");

        key1.Should().Be(key2);
        key1.Length.Should().BeLessThanOrEqualTo(100);
        key1.Should().StartWith("omro:aaaaaaaabbbbccccddddeeeeeeeeeeee:i:0:");

        // Compatible with MedCareOS IdempotencyKeyFilter 200-char "{endpoint}:{key}" limit
        ($"/api/resolutions/propose:{key1}").Length.Should().BeLessThanOrEqualTo(200);
    }

    [Fact]
    public void Generate_ShouldDifferAcrossPhaseIndexTaskActionMethodAndEndpoint()
    {
        var taskA = Guid.NewGuid();
        var taskB = Guid.NewGuid();

        var baseKey = _generator.Generate(taskA, ActionExecutionPhase.Immediate, 0, "REROUTE", "POST", "https://tenant.test/api/reroute");
        var deferredKey = _generator.Generate(taskA, ActionExecutionPhase.Deferred, 0, "REROUTE", "POST", "https://tenant.test/api/reroute");
        var index1Key = _generator.Generate(taskA, ActionExecutionPhase.Immediate, 1, "REROUTE", "POST", "https://tenant.test/api/reroute");
        var otherTaskKey = _generator.Generate(taskB, ActionExecutionPhase.Immediate, 0, "REROUTE", "POST", "https://tenant.test/api/reroute");
        var otherActionKey = _generator.Generate(taskA, ActionExecutionPhase.Immediate, 0, "ESCALATE", "POST", "https://tenant.test/api/reroute");
        var otherMethodKey = _generator.Generate(taskA, ActionExecutionPhase.Immediate, 0, "REROUTE", "PUT", "https://tenant.test/api/reroute");
        var otherEndpointKey = _generator.Generate(taskA, ActionExecutionPhase.Immediate, 0, "REROUTE", "POST", "https://tenant.test/api/reroute-v2");

        var distinctKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            baseKey,
            deferredKey,
            index1Key,
            otherTaskKey,
            otherActionKey,
            otherMethodKey,
            otherEndpointKey
        };

        distinctKeys.Should().HaveCount(7);
    }
}
