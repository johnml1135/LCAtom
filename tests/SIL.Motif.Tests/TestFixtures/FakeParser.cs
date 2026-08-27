using System.Text.Json;
using SIL.Motif.Generator;

namespace SIL.Motif.Tests.TestFixtures;

/// <summary>
/// Points a test at the fake <c>pangloss</c> executable and writes the behaviour it should take.
/// </summary>
/// <remarks>
/// The fake honours the real command contract, so a test using it exercises the genuine process boundary
/// — argument building, stream draining, exit codes, cancellation and report parsing — without a Rust
/// build or a grammar a parser would accept. Behaviour travels in a file beside the grammar source rather
/// than in the environment, so parallel test classes cannot see each other's settings.
/// </remarks>
internal static class FakeParser
{
    private const string BehaviourFileName = "_fake-pangloss.json";

    /// <summary>The fake parser's path, built alongside the test project.</summary>
    internal static string ExecutablePath
    {
        get
        {
            var name = OperatingSystem.IsWindows() ? "pangloss.exe" : "pangloss";
            var path = Path.Combine(RepoPaths.FindRepoRoot(), "tests", "FakePanGloss", "bin", "Debug",
                "net10.0", name);
            if (!File.Exists(path))
                throw new FileNotFoundException("The fake parser was not built beside the tests.", path);
            return path;
        }
    }

    /// <summary>Tells the fake how to behave for candidates exported into this directory.</summary>
    internal static void Behave(string candidateDirectory, object behaviour) =>
        File.WriteAllText(Path.Combine(candidateDirectory, BehaviourFileName),
            JsonSerializer.Serialize(behaviour));
}
