using System.Xml.Linq;
using SIL.Motif.Generator;
using Xunit;

namespace SIL.Motif.Tests.Contract;

/// <summary>
/// Pins which projects target <c>netstandard2.0</c>, read from the project files themselves.
/// </summary>
/// <remarks>
/// The rule is consumption, not convention: a project keeps <c>netstandard2.0</c> only while something
/// outside Motif references the assembly. Today that is <c>SIL.Motif.Contract</c> alone — a <c>net48</c>
/// FieldWorks surface and the non-.NET runners deserialise <c>motif --json</c> with it. Everything else
/// runs in a Motif process and can use the current framework.
///
/// This is a test rather than a convention because the target creeps back silently: the next person who
/// wants a compatibility shim adds the moniker, nothing fails, and the reason it was retired is lost.
/// </remarks>
public sealed class CompatibilityTargetTests
{
    private static readonly string[] CrossesIntoAForeignHost = { "SIL.Motif.Contract" };

    [Fact]
    public void OnlyProjectsConsumedOutsideMotifTargetNetstandard()
    {
        var offenders = new List<string>();
        foreach (var (name, targets) in ProjectTargets())
        {
            var declaresNetstandard = targets.Contains("netstandard2.0", StringComparison.Ordinal);
            var mayDeclareIt = CrossesIntoAForeignHost.Contains(name, StringComparer.Ordinal);
            if (declaresNetstandard != mayDeclareIt)
                offenders.Add(name + " => " + targets);
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void NoProjectTargetsNetFrameworkOrNetEight()
    {
        foreach (var (name, targets) in ProjectTargets())
        {
            Assert.DoesNotContain("net48", targets, StringComparison.Ordinal);
            Assert.DoesNotContain("net8.0", targets, StringComparison.Ordinal);
        }
    }

    private static IEnumerable<(string Name, string Targets)> ProjectTargets()
    {
        var source = Path.Combine(RepoPaths.FindRepoRoot(), "src");
        foreach (var project in Directory.EnumerateFiles(source, "*.csproj", SearchOption.AllDirectories))
        {
            var document = XDocument.Load(project);
            var single = document.Descendants("TargetFramework").FirstOrDefault()?.Value;
            var multiple = document.Descendants("TargetFrameworks").FirstOrDefault()?.Value;
            yield return (Path.GetFileNameWithoutExtension(project), multiple ?? single ?? string.Empty);
        }
    }
}
