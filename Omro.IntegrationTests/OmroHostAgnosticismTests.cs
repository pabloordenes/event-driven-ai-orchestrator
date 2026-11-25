using System.Reflection;
using System.Runtime.CompilerServices;
using FluentAssertions;

namespace Omro.IntegrationTests;

public class OmroHostAgnosticismTests
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

    private static readonly string[] ForbiddenHardcodedLiterals =
    [
        "Clínica San José",
        "medcareos-api",
        "onrender.com",
        "AppointmentNoShowRisk",
        "GENERATE_OVERBOOK",
        "PROPOSE_RESOLUTION",
        "MedCare"
    ];

    private static readonly string[] ProductionProjectDirs =
    [
        "Omro.Contracts",
        "Omro.Domain",
        "Omro.Application",
        "Omro.Infrastructure",
        "Omro.Worker",
        "Omro.Api"
    ];

    [Fact]
    public void HostAssemblies_ShouldNotContainClinicalIdentifiersInTypesPropertiesOrMethods()
    {
        var assemblies = new[]
        {
            typeof(Omro.Worker.Consumers.AnomalyEventConsumer).Assembly,
            typeof(Omro.Api.Services.IOmroQueueAdminService).Assembly
        };

        var violations = new List<string>();

        foreach (var assembly in assemblies)
        {
            foreach (var type in assembly.GetTypes())
            {
                if (type.IsDefined(typeof(CompilerGeneratedAttribute), false))
                    continue;

                CheckSymbol(assembly.GetName().Name!, type.FullName ?? type.Name, "Type", violations);

                foreach (var prop in type.GetProperties(
                             BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                             BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    CheckSymbol(assembly.GetName().Name!, $"{type.Name}.{prop.Name}", "Property", violations);
                }

                foreach (var method in type.GetMethods(
                             BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                             BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    if (method.IsSpecialName || method.IsDefined(typeof(CompilerGeneratedAttribute), false))
                        continue;

                    CheckSymbol(assembly.GetName().Name!, $"{type.Name}.{method.Name}", "Method", violations);
                }
            }
        }

        violations.Should().BeEmpty("los hosts Omro.Worker y Omro.Api deben mantenerse 100% agnósticos del dominio clínico");
    }

    [Fact]
    public void ProductionSourceFiles_ShouldNotContainHardcodedTenantLiterals()
    {
        var repoRoot = FindRepoRoot();
        repoRoot.Should().NotBeNull("debe localizarse la raíz del repositorio con Omro.slnx");

        var violations = new List<string>();

        foreach (var projectDir in ProductionProjectDirs)
        {
            var fullProjectDir = Path.Combine(repoRoot!, projectDir);
            Directory.Exists(fullProjectDir).Should().BeTrue($"el proyecto {projectDir} debe existir");

            var sourceFiles = Directory.EnumerateFiles(fullProjectDir, "*.cs", SearchOption.AllDirectories)
                .Where(path => !IsExcludedPath(path));

            foreach (var file in sourceFiles)
            {
                var content = File.ReadAllText(file);
                foreach (var literal in ForbiddenHardcodedLiterals)
                {
                    if (content.Contains(literal, StringComparison.OrdinalIgnoreCase))
                    {
                        var relative = Path.GetRelativePath(repoRoot!, file);
                        violations.Add($"{relative}: contiene literal prohibido '{literal}'");
                    }
                }
            }
        }

        violations.Should().BeEmpty("el código C# de producción de OMRO no debe contener reglas, endpoints ni nombres de tenant hardcodeados");
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

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Omro.slnx")))
                return dir.FullName;

            dir = dir.Parent;
        }

        return null;
    }

    private static bool IsExcludedPath(string fullPath)
    {
        var normalized = fullPath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        return normalized.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains($"{Path.DirectorySeparatorChar}Seeds{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
    }
}
