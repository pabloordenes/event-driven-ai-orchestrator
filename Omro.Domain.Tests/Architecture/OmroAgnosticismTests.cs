using System.Reflection;
using System.Runtime.CompilerServices;
using FluentAssertions;

namespace Omro.Domain.Tests.Architecture;

public class OmroAgnosticismTests
{
    private static readonly string[] ForbiddenClinicalTerms =
    [
        "Appointment",
        "Patient",
        "Cita",
        "Paciente",
        "Sobrecupo",
        "Overbook",
        "WaitingList",
        "ScheduleBlock",
        "MedCare"
    ];

    [Fact]
    public void OmroAssemblies_ShouldNotContainClinicalIdentifiersInTypesPropertiesOrMethods()
    {
        var assemblies = new[]
        {
            typeof(Omro.Domain.ResolutionTasks.ResolutionTask).Assembly,
            typeof(Omro.Application.Interfaces.ICognitiveOrchestrator).Assembly,
            typeof(Omro.Infrastructure.CognitiveServices.Agents.GroqCognitiveOrchestrator).Assembly
        };

        var violations = new List<string>();

        foreach (var assembly in assemblies)
        {
            foreach (var type in assembly.GetTypes())
            {
                if (type.IsDefined(typeof(CompilerGeneratedAttribute), false))
                    continue;

                CheckSymbol(assembly.GetName().Name!, type.FullName ?? type.Name, "Type", violations);

                foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    CheckSymbol(assembly.GetName().Name!, $"{type.Name}.{prop.Name}", "Property", violations);
                }

                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    if (method.IsSpecialName || method.IsDefined(typeof(CompilerGeneratedAttribute), false))
                        continue;

                    CheckSymbol(assembly.GetName().Name!, $"{type.Name}.{method.Name}", "Method", violations);
                }
            }
        }

        violations.Should().BeEmpty("OMRO debe mantenerse 100% agnóstico del dominio clínico");
    }

    private static void CheckSymbol(string assemblyName, string symbol, string kind, List<string> violations)
    {
        foreach (var term in ForbiddenClinicalTerms)
        {
            if (symbol.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                violations.Add($"{assemblyName} {kind}: {symbol} (matched '{term}')");
            }
        }
    }
}
